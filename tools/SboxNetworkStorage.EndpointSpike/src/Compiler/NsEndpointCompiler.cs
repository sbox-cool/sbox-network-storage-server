using System.Globalization;
using System.Text;
using Acornima;
using Acornima.Ast;
using SboxNetworkStorage.EndpointSpike.Plan;

namespace SboxNetworkStorage.EndpointSpike.Compiler;

/// <summary>1-based source position of a compiler-owned step id.</summary>
public sealed record SourceSpan(int Line, int Column);

/// <summary>
/// Immutable compiler output. <see cref="Plan"/> is the executable endpoint definition consumed by
/// the existing step executor; the remaining fields form the stored envelope.
/// </summary>
public sealed record CompiledEndpoint(
    string EndpointId,
    Dictionary<string, object?> Plan,
    string PlanJson,
    string PlanSha256,
    string Source,
    string SourceSha256,
    IReadOnlyDictionary<string, SourceSpan> SourceMap,
    int StaticOps)
{
    public const string SourceFormat = "ns-endpoint";
    public const int SourceVersion = 1;
    public const string LanguageId = "ns-endpoint/1";
    public const string CompilerVersion = "0.1.0-spike";
}

/// <summary>
/// Compiles the fixed <c>ns-endpoint/1</c> grammar to an executable plan. The source is parsed with
/// Acornima and walked by an allow-list; it is never evaluated. The first violation aborts compilation.
/// </summary>
public static class NsEndpointCompiler
{
    public static CompileOutcome Compile(string source)
    {
        try
        {
            return CompileOutcome.Success(CompileOrThrow(source));
        }
        catch (NsCompileException ex)
        {
            return CompileOutcome.Fail(ex.Diagnostic);
        }
    }

    internal static CompiledEndpoint CompileOrThrow(string source)
    {
        var byteCount = Encoding.UTF8.GetByteCount(source);
        if (byteCount > NsLimits.MaxSourceBytes)
            throw new NsCompileException(new NsDiagnostic("SOURCE_TOO_LARGE",
                $"Source is {byteCount} bytes; the limit is {NsLimits.MaxSourceBytes}.", 1, 1));

        PreScan(source);

        Script script;
        try
        {
            script = JsAst.ParseScript(source);
        }
        catch (SyntaxErrorException ex)
        {
            // Acornima's syntax error location is not consumed by this spike; the message carries it.
            throw new NsCompileException(new NsDiagnostic("SYNTAX_ERROR", ex.Message, 0, 0));
        }

        CheckAstBounds(script);

        var program = new Lowerer().Lower(script);
        if (program.StaticOps > NsLimits.MaxStaticExpressionOps)
            throw new NsCompileException(new NsDiagnostic("BUDGET_OPS_EXCEEDED",
                $"Static operation charge {program.StaticOps} exceeds {NsLimits.MaxStaticExpressionOps}.", 1, 1));

        var plan = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = program.Id,
            ["method"] = program.Method,
            ["enabled"] = true,
            ["input"] = program.Input,
            ["collections"] = program.Collections.Cast<object?>().ToList(),
            ["steps"] = program.Steps.Cast<object?>().ToList(),
            ["response"] = program.Response,
        };

        var planJson = CanonicalJson.Serialize(plan);
        if (Encoding.UTF8.GetByteCount(planJson) > NsLimits.MaxPlanBytes)
            throw new NsCompileException(new NsDiagnostic("PLAN_TOO_LARGE",
                $"Canonical plan exceeds {NsLimits.MaxPlanBytes} bytes.", 1, 1));

        return new CompiledEndpoint(
            EndpointId: program.Id,
            Plan: plan,
            PlanJson: planJson,
            PlanSha256: CanonicalJson.Sha256Hex(planJson),
            Source: source,
            SourceSha256: CanonicalJson.Sha256Hex(source),
            SourceMap: program.SourceMap,
            StaticOps: program.StaticOps);
    }

    /// <summary>
    /// Lexical checks before parsing: no template literals, and bracket depth bounded so the parser
    /// never recurses past the nesting limit. Strings and comments are skipped; this only tightens checks.
    /// </summary>
    private static void PreScan(string source)
    {
        if (source.IndexOf('`') is var tick and >= 0)
            throw At("TEMPLATE_UNSUPPORTED", "Template literals are not supported; use plain string literals.", source, tick);

        var depth = 0;
        var quote = '\0';
        for (var i = 0; i < source.Length; i++)
        {
            var ch = source[i];
            if (quote != '\0')
            {
                if (ch == '\\') i++;
                else if (ch == quote || ch == '\n') quote = '\0';
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
            }
            else if (ch == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
            }
            else if (ch == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) break;
                i = close + 1;
            }
            else if (ch is '(' or '[' or '{')
            {
                depth++;
                if (depth > NsLimits.MaxNestingDepth)
                    throw At("DEPTH_EXCEEDED", $"Nesting depth exceeds {NsLimits.MaxNestingDepth}.", source, i);
            }
            else if (ch is ')' or ']' or '}')
            {
                depth--;
            }
        }
    }

    /// <summary>Counts AST nodes and depth with early exit; runs before any lowering.</summary>
    private static void CheckAstBounds(Script script)
    {
        var count = 0;
        var visited = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        void Walk(Node node, int depth)
        {
            if (!visited.Add(node)) return;
            count++;
            if (count > NsLimits.MaxAstNodes)
                throw Fail("AST_NODES_EXCEEDED", $"Source has more than {NsLimits.MaxAstNodes} syntax nodes.", node);
            if (depth > NsLimits.MaxNestingDepth)
                throw Fail("DEPTH_EXCEEDED", $"Nesting depth exceeds {NsLimits.MaxNestingDepth}.", node);
            foreach (var child in JsAst.Children(node)) Walk(child, depth + 1);
        }

        Walk(script, 1);
    }

    internal static NsCompileException Fail(string code, string message, Node node)
    {
        var (line, column) = JsAst.Span(node);
        return new NsCompileException(new NsDiagnostic(code, message, line, column));
    }

    private static NsCompileException At(string code, string message, string source, int index)
    {
        var line = 1;
        var lineStart = 0;
        for (var i = 0; i < index && i < source.Length; i++)
        {
            if (source[i] != '\n') continue;
            line++;
            lineStart = i + 1;
        }

        return new NsCompileException(new NsDiagnostic(code, message, line, index - lineStart + 1));
    }

    internal sealed record LoweredProgram(
        string Id,
        string Method,
        Dictionary<string, object?> Input,
        List<string> Collections,
        List<Dictionary<string, object?>> Steps,
        Dictionary<string, object?> Response,
        Dictionary<string, SourceSpan> SourceMap,
        int StaticOps);

    /// <summary>
    /// Allow-list lowering. Every accepted node maps to a fixed step shape; anything else is a diagnostic.
    /// Local names become compiler-owned step ids; comparisons and arithmetic become executor expressions.
    /// </summary>
    internal sealed class Lowerer
    {
        private static readonly HashSet<string> CapabilityNames = new(StringComparer.Ordinal)
        {
            "input", "steamId", "now", "values", "read", "write", "reject", "exists", "floor", "abs",
        };

        private static readonly HashSet<string> FunctionNames = new(StringComparer.Ordinal)
        {
            "read", "write", "reject", "exists", "floor", "abs",
        };

        private static readonly HashSet<string> ReservedConstNames = new(StringComparer.Ordinal)
        {
            "input", "steamId", "now", "values", "read", "write", "reject", "exists", "floor", "abs",
            "endpoint", "projectId", "userId", "playerKey",
        };

        private static readonly HashSet<string> DataRoots = new(StringComparer.Ordinal)
        {
            "input", "steamId", "now", "values",
        };

        private readonly List<Dictionary<string, object?>> _steps = new();
        private readonly Dictionary<string, SourceSpan> _sourceMap = new(StringComparer.Ordinal);
        private readonly List<string> _collections = new();
        private readonly HashSet<string> _scope = new(StringComparer.Ordinal);
        private int _guards;
        private int _temps;
        private int _ops;
        private bool _writeSeen;
        private Dictionary<string, object?>? _response;

        public LoweredProgram Lower(Script script)
        {
            var statements = script.Body.ToArray();
            if (statements is not [ExpressionStatement { Expression: CallExpression endpointCall }]
                || endpointCall.Callee is not Identifier { Name: "endpoint" })
                throw Fail("ENDPOINT_CALL_REQUIRED", "Source must be a single endpoint(metadata, handler) expression.", script);

            var args = endpointCall.Arguments.ToArray();
            if (args.Length != 2 || args[0] is not ObjectExpression metadataObject)
                throw Fail("ENDPOINT_CALL_REQUIRED", "endpoint(...) takes a metadata object and a handler function.", endpointCall);

            var (id, method, input) = ReadMetadata(metadataObject);

            if (args[1] is not ArrowFunctionExpression handler || handler.Async || handler.Expression
                || handler.Body is not BlockStatement block)
                throw Fail("ENDPOINT_CALL_REQUIRED", "The handler must be an arrow function with a block body.", args[1]);

            var parameters = handler.Params.ToArray();
            if (parameters is not [ObjectPattern pattern])
                throw Fail("ENDPOINT_CALL_REQUIRED", "The handler takes exactly one destructured capability parameter.", handler);

            foreach (var item in pattern.Properties)
            {
                if (item is not Property { Value: Identifier bound, Computed: false } prop
                    || prop.Key is not Identifier key
                    || key.Name != bound.Name)
                    throw Fail("UNSUPPORTED_SYNTAX", "Capability parameters must be plain shorthand names.", item);
                if (!CapabilityNames.Contains(key.Name))
                    throw Fail("UNDECLARED_IDENTIFIER", $"\"{key.Name}\" is not an available capability.", item);
                if (key.Name is "read" or "write" or "reject" or "exists" or "floor" or "abs")
                {
                    // Function capabilities are referenced in call position only; they are not data names.
                    continue;
                }

                _scope.Add(key.Name);
            }

            LowerBody(block);

            if (_response is null)
                throw Fail("RETURN_REQUIRED", "The handler must end with return { status, body }.", block);

            if (_steps.Count + _ops > NsLimits.MaxStaticExpressionOps)
                throw Fail("BUDGET_OPS_EXCEEDED", "Static operation charge exceeds the limit.", block);

            return new LoweredProgram(
                id,
                method,
                input,
                _collections.Distinct(StringComparer.Ordinal).ToList(),
                _steps,
                _response,
                _sourceMap,
                _ops + _steps.Count);
        }

        private (string Id, string Method, Dictionary<string, object?> Input) ReadMetadata(ObjectExpression metadata)
        {
            string? id = null;
            string? method = null;
            Dictionary<string, object?>? input = null;
            foreach (var entry in Entries(metadata, requireIdentifierKeys: false))
            {
                switch (entry.Key)
                {
                    case "id":
                        id = entry.Value is Literal { Value: string text } && text.Length is > 0 and <= 128
                            ? text
                            : throw Fail("INVALID_ENDPOINT_METADATA", "id must be a string literal of 1-128 characters.", entry.Node);
                        break;
                    case "method":
                        method = entry.Value is Literal { Value: string verb } && verb is "GET" or "POST" or "PUT" or "PATCH" or "DELETE"
                            ? verb
                            : throw Fail("INVALID_ENDPOINT_METADATA", "method must be one of GET, POST, PUT, PATCH, DELETE.", entry.Node);
                        break;
                    case "input":
                        input = LiteralTree(entry.Value) as Dictionary<string, object?>
                                ?? throw Fail("INVALID_ENDPOINT_METADATA", "input must be a literal object.", entry.Node);
                        break;
                    default:
                        throw Fail("INVALID_ENDPOINT_METADATA", $"Unknown metadata key \"{entry.Key}\".", entry.Node);
                }
            }

            if (id is null || method is null || input is null)
                throw Fail("INVALID_ENDPOINT_METADATA", "Metadata requires id, method and input.", metadata);

            return (id, method, input);
        }

        /// <summary>Metadata is literal data only; no names, calls or operators.</summary>
        private object? LiteralTree(Expression node)
        {
            switch (node)
            {
                case Literal { Value: string or bool or null } literal:
                    return literal.Value;
                case Literal { Value: double number } numeric when double.IsFinite(number):
                    return number;
                case UnaryExpression { Argument: Literal { Value: double negated } } unary when JsAst.OperatorText(unary.Operator) == "-" && double.IsFinite(negated):
                    return -negated;
                case ArrayExpression array:
                {
                    var items = new List<object?>();
                    foreach (var element in array.Elements)
                    {
                        if (element is null) throw Fail("INVALID_ENDPOINT_METADATA", "Array holes are not allowed.", array);
                        items.Add(LiteralTree(element));
                    }

                    return items;
                }
                case ObjectExpression obj:
                {
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var entry in Entries(obj, requireIdentifierKeys: false))
                        map[entry.Key] = LiteralTree(entry.Value);
                    return map;
                }
                default:
                    throw Fail("INVALID_ENDPOINT_METADATA", "Metadata must be literal data.", node);
            }
        }

        private void LowerBody(BlockStatement block)
        {
            var returned = false;
            foreach (var statement in block.Body)
            {
                if (returned)
                    throw Fail("RETURN_NOT_LAST", "return must be the final statement.", statement);

                switch (statement)
                {
                    case ReturnStatement ret:
                        LowerReturn(ret);
                        returned = true;
                        break;
                    case VariableDeclaration declaration:
                        LowerDeclaration(declaration);
                        break;
                    case IfStatement guard:
                        LowerGuard(guard);
                        break;
                    case ExpressionStatement { Expression: CallExpression call } when IsCall(call, "write"):
                        LowerWrite(call);
                        break;
                    default:
                        throw Unsupported(statement);
                }
            }

            if (!returned)
                throw Fail("RETURN_REQUIRED", "The handler must end with return { status, body }.", block);
        }

        private void LowerDeclaration(VariableDeclaration declaration)
        {
            if (_writeSeen)
                throw Fail("WRITE_ORDER", "Declarations must come before the write.", declaration);
            if (JsAst.KindText(declaration.Kind) != "const")
                throw Fail("UNSUPPORTED_SYNTAX", "Only const declarations are supported.", declaration);

            var declarators = declaration.Declarations.ToArray();
            if (declarators is not [{ Id: Identifier name, Init: not null } declarator])
                throw Fail("UNSUPPORTED_SYNTAX", "Declare one named const per statement with an initializer.", declaration);

            CheckDeclarableName(name.Name, name);

            switch (declarator.Init)
            {
                case CallExpression call when IsCall(call, "read"):
                    LowerRead(name.Name, call, declarator);
                    break;
                case CallExpression call when IsValuesFind(call):
                    LowerLookup(name.Name, call, declarator);
                    break;
                default:
                    LowerCompute(name.Name, declarator.Init, declarator);
                    break;
            }

            _scope.Add(name.Name);
        }

        private void CheckDeclarableName(string name, Node node)
        {
            if (name.StartsWith('_') || name.StartsWith("guard_", StringComparison.Ordinal)
                || name.StartsWith("write_", StringComparison.Ordinal) || ReservedConstNames.Contains(name))
                throw Fail("RESERVED_NAME", $"\"{name}\" is reserved by ns-endpoint/1.", node);
            if (_scope.Contains(name))
                throw Fail("DUPLICATE_NAME", $"\"{name}\" is already declared.", node);
        }

        private void LowerRead(string name, CallExpression call, Node node)
        {
            var args = call.Arguments.ToArray();
            if (args.Length != 2)
                throw Fail("READ_ARGUMENTS", "read(collection, key) takes two arguments.", call);

            var collection = CollectionName(args[0]);
            var key = Key(args[1]);
            AddStep(name, node, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = name,
                ["type"] = "read",
                ["collection"] = collection,
                ["key"] = key,
            });
            _collections.Add(collection);
        }

        private void LowerLookup(string name, CallExpression call, Node node)
        {
            var args = call.Arguments.ToArray();
            if (args.Length != 4)
                throw Fail("LOOKUP_ARGUMENTS", "values.find(table, field, value, { status, code }) takes four arguments.", call);

            if (args[0] is not Literal { Value: string table } || table.Length == 0 || HasTemplateChars(table))
                throw Fail("LOOKUP_ARGUMENTS", "The table must be a plain string literal.", args[0]);
            if (args[1] is not Literal { Value: string field } || !IsIdentifierName(field))
                throw Fail("LOOKUP_ARGUMENTS", "The field must be a plain column name string literal.", args[1]);
            var value = Template(args[2]);

            if (args[3] is not ObjectExpression options)
                throw Fail("LOOKUP_ARGUMENTS", "The missing-row error must be an object { status, code }.", args[3]);
            var (status, code) = MissingRowError(options);

            AddStep(name, node, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = name,
                ["type"] = "lookup",
                ["source"] = "values",
                ["table"] = table,
                ["where"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["field"] = field,
                    ["op"] = "==",
                    ["value"] = value,
                },
                ["required"] = true,
                ["onMissing"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["status"] = status,
                    ["errorCode"] = code,
                },
            });
        }

        private (double Status, string Code) MissingRowError(ObjectExpression options)
        {
            double? status = null;
            string? code = null;
            foreach (var entry in Entries(options, requireIdentifierKeys: true))
            {
                switch (entry.Key)
                {
                    case "status":
                        status = StatusLiteral(entry.Value, entry.Node);
                        break;
                    case "code":
                        code = PlainText(entry.Value, entry.Node);
                        break;
                    default:
                        throw Fail("LOOKUP_ARGUMENTS", $"Unknown missing-row option \"{entry.Key}\".", entry.Node);
                }
            }

            return status is { } s && code is not null
                ? (s, code)
                : throw Fail("LOOKUP_ARGUMENTS", "The missing-row error requires status and code.", options);
        }

        private void LowerCompute(string name, Expression init, Node node)
        {
            var text = Arithmetic(init);
            if (!text.Contains("{{", StringComparison.Ordinal))
                throw Fail("LITERAL_CONST_UNSUPPORTED", "A const must depend on a path; literal-only constants are not lowered.", init);

            AddStep(name, node, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = name,
                ["type"] = "compute",
                ["output"] = "scalars",
                ["values"] = new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = text },
            });
        }

        private void LowerGuard(IfStatement guard)
        {
            if (_writeSeen)
                throw Fail("WRITE_ORDER", "Guards must come before the write.", guard);
            if (guard.Alternate is not null)
                throw Fail("UNSUPPORTED_SYNTAX", "else is not supported; write one if per rejection.", guard);

            var rejection = RejectCall(guard.Consequent);
            var args = rejection.Arguments.ToArray();
            if (args.Length is < 2 or > 3)
                throw Fail("REJECT_ARGUMENTS", "reject(status, code, message?) takes two or three arguments.", rejection);

            var status = StatusLiteral(args[0], args[0]);
            var code = PlainText(args[1], args[1]);
            var onFail = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = status,
                ["error"] = code,
            };
            if (args.Length == 3)
                onFail["message"] = PlainText(args[2], args[2]);

            var check = NegatePredicate(Predicate(guard.Test));
            var id = "guard_" + (++_guards).ToString(CultureInfo.InvariantCulture);
            AddStep(id, guard, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["type"] = "condition",
                ["check"] = check,
                ["onFail"] = onFail,
            });
        }

        private CallExpression RejectCall(Statement consequent)
        {
            var statement = consequent;
            if (consequent is BlockStatement block)
            {
                var inner = block.Body.ToArray();
                if (inner is not [var only])
                    throw Fail("UNSUPPORTED_SYNTAX", "An if may contain only one reject(...) call.", block);
                statement = only;
            }

            if (statement is ExpressionStatement { Expression: CallExpression call } && IsCall(call, "reject"))
                return call;
            throw Fail("UNSUPPORTED_SYNTAX", "An if must be followed by reject(status, code, message?).", consequent);
        }

        private void LowerWrite(CallExpression call)
        {
            if (_writeSeen)
                throw Fail("MULTIPLE_WRITES", "Only one write is allowed per endpoint.", call);

            var args = call.Arguments.ToArray();
            if (args.Length != 3 || args[2] is not ObjectExpression options)
                throw Fail("WRITE_ARGUMENTS", "write(collection, key, { inc?, set? }) takes three arguments.", call);

            var collection = CollectionName(args[0]);
            var key = Key(args[1]);

            var ops = new List<object?>();
            var entries = Entries(options, requireIdentifierKeys: true).ToArray();
            if (entries.Length == 0 || entries.Any(e => e.Key is not ("inc" or "set")))
                throw Fail("WRITE_ARGUMENTS", "write options must be inc and/or set.", options);

            foreach (var group in entries.Where(e => e.Key == "inc"))
            {
                if (group.Value is not ObjectExpression incObject)
                    throw Fail("WRITE_ARGUMENTS", "inc must be an object of path: amount.", group.Node);
                foreach (var inc in Entries(incObject, requireIdentifierKeys: true))
                {
                    ops.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["op"] = "inc",
                        ["path"] = inc.Key,
                        ["value"] = Template(inc.Value),
                    });
                }
            }

            foreach (var group in entries.Where(e => e.Key == "set"))
            {
                if (group.Value is not ObjectExpression setObject)
                    throw Fail("WRITE_ARGUMENTS", "set must be an object of path: value.", group.Node);
                foreach (var set in Entries(setObject, requireIdentifierKeys: true))
                {
                    ops.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["op"] = "set",
                        ["path"] = set.Key,
                        ["value"] = WriteTree(set.Value),
                    });
                }
            }

            _writeSeen = true;
            _collections.Add(collection);
            AddStep("write_1", call, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "write_1",
                ["type"] = "write",
                ["collection"] = collection,
                ["key"] = key,
                ["ops"] = ops,
            });
        }

        private object? WriteTree(Expression value)
        {
            if (value is ObjectExpression obj)
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var entry in Entries(obj, requireIdentifierKeys: true))
                    map[entry.Key] = WriteTree(entry.Value);
                return map;
            }

            if (value is ArrayExpression array)
            {
                var items = new List<object?>();
                foreach (var element in array.Elements)
                {
                    if (element is null) throw Fail("UNSUPPORTED_SYNTAX", "Array holes are not allowed.", array);
                    items.Add(WriteTree(element));
                }

                return items;
            }

            return Template(value);
        }

        private void LowerReturn(ReturnStatement ret)
        {
            if (ret.Argument is not ObjectExpression shape)
                throw Fail("RESPONSE_SHAPE", "return must be { status, body }.", ret);

            double? status = null;
            object? body = null;
            var hasBody = false;
            foreach (var entry in Entries(shape, requireIdentifierKeys: true))
            {
                switch (entry.Key)
                {
                    case "status":
                        status = StatusLiteral(entry.Value, entry.Node);
                        break;
                    case "body":
                        body = ResponseLeaf(entry.Value);
                        hasBody = true;
                        break;
                    default:
                        throw Fail("RESPONSE_SHAPE", $"Unknown response key \"{entry.Key}\".", entry.Node);
                }
            }

            if (status is null || !hasBody)
                throw Fail("RESPONSE_SHAPE", "return requires status and body.", ret);

            _response = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["status"] = status,
                ["body"] = body,
            };
            _sourceMap["response"] = Span(ret);
        }

        private object? ResponseLeaf(Expression value)
        {
            switch (value)
            {
                case ObjectExpression obj:
                {
                    var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var entry in Entries(obj, requireIdentifierKeys: true))
                        map[entry.Key] = ResponseLeaf(entry.Value);
                    return map;
                }
                case ArrayExpression array:
                {
                    var items = new List<object?>();
                    foreach (var element in array.Elements)
                    {
                        if (element is null) throw Fail("UNSUPPORTED_SYNTAX", "Array holes are not allowed.", array);
                        items.Add(ResponseLeaf(element));
                    }

                    return items;
                }
                case Literal { Value: null }:
                    return null;
                case Literal { Value: bool flag }:
                    return flag;
                case Literal { Value: double number } when double.IsFinite(number):
                    return number;
                case Literal { Value: string text }:
                    return PlainText(value, value, text);
                case Identifier or MemberExpression:
                    return "{{" + Path(value) + "}}";
                default:
                    throw Fail("EXPRESSION_UNSUPPORTED",
                        "Response leaves must be a path or a literal; compute the value in a const first.", value);
            }
        }

        private Dictionary<string, object?> Predicate(Expression test)
        {
            switch (test)
            {
                case LogicalExpression logical:
                {
                    var op = JsAst.OperatorText(logical.Operator);
                    _ops++;
                    if (op == "&&")
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["all"] = new List<object?> { Predicate(logical.Left), Predicate(logical.Right) },
                        };
                    if (op == "||")
                        return new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["any"] = new List<object?> { Predicate(logical.Left), Predicate(logical.Right) },
                        };
                    throw Fail("PREDICATE_UNSUPPORTED", "Only && and || combine conditions.", logical);
                }
                case CallExpression call when IsCall(call, "exists"):
                    return Existence(call, "exists");
                case UnaryExpression { Argument: CallExpression negated } unary
                    when JsAst.OperatorText(unary.Operator) == "!" && IsCall(negated, "exists"):
                    return Existence(negated, "not_exists");
                case BinaryExpression comparison:
                    return Comparison(comparison);
                default:
                    throw Fail("PREDICATE_UNSUPPORTED",
                        "A condition must be a comparison, exists(path), !exists(path), or && / || of those.", test);
            }
        }

        private Dictionary<string, object?> Existence(CallExpression call, string op)
        {
            var args = call.Arguments.ToArray();
            if (args.Length != 1 || !IsPathNode(args[0]))
                throw Fail("PREDICATE_UNSUPPORTED", "exists takes one path argument.", call);
            _ops++;
            return Check(Path(args[0]), op, null, hasValue: false);
        }

        private static Dictionary<string, object?> NegatePredicate(Dictionary<string, object?> predicate)
        {
            if (predicate.TryGetValue("all", out var allRaw) && allRaw is List<object?> all)
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["any"] = all.Select(item => NegatePredicate((Dictionary<string, object?>)item!)).Cast<object?>().ToList(),
                };
            if (predicate.TryGetValue("any", out var anyRaw) && anyRaw is List<object?> any)
                return new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["all"] = any.Select(item => NegatePredicate((Dictionary<string, object?>)item!)).Cast<object?>().ToList(),
                };

            var inverted = predicate.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            inverted["op"] = (string)predicate["op"]! switch
            {
                "<" => ">=",
                "<=" => ">",
                ">" => "<=",
                ">=" => "<",
                "==" => "!=",
                "!=" => "==",
                "exists" => "not_exists",
                "not_exists" => "exists",
                var op => throw new InvalidOperationException($"Cannot negate predicate operator {op}."),
            };
            return inverted;
        }

        private Dictionary<string, object?> Comparison(BinaryExpression comparison)
        {
            var op = JsAst.OperatorText(comparison.Operator);
            if (op is "===" or "!==")
                throw Fail("PREDICATE_UNSUPPORTED", "Use == or != in ns-endpoint/1.", comparison);
            if (op is not ("<" or "<=" or ">" or ">=" or "==" or "!="))
                throw Fail("PREDICATE_UNSUPPORTED", "Unsupported comparison operator.", comparison);

            _ops++;
            var left = comparison.Left;
            var right = comparison.Right;
            if (IsPathNode(left))
                return Check(Path(left), op!, Template(right));
            if (IsPathNode(right))
                return Check(Path(right), Flip(op!), Template(left));
            if (IsLiteral(left) && IsLiteral(right))
                throw Fail("PREDICATE_UNSUPPORTED", "A comparison must reference a path.", comparison);
            if (IsLiteral(right))
                return Check(Hoist(left), op!, Template(right));
            return Check(Hoist(right), Flip(op!), Template(left));
        }

        private Dictionary<string, object?> Check(string field, string op, object? value, bool hasValue = true)
        {
            var check = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["field"] = field,
                ["op"] = op,
            };
            if (hasValue) check["value"] = value;
            return check;
        }

        /// <summary>Evaluates a complex comparison operand into a compiler-owned scalar step first.</summary>
        private string Hoist(Expression operand)
        {
            var text = Arithmetic(operand);
            if (!text.Contains("{{", StringComparison.Ordinal))
                throw Fail("LITERAL_CONST_UNSUPPORTED", "A comparison operand must reference a path.", operand);

            var name = "_t" + (++_temps).ToString(CultureInfo.InvariantCulture);
            AddStep(name, operand, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = name,
                ["type"] = "compute",
                ["output"] = "scalars",
                ["values"] = new Dictionary<string, object?>(StringComparer.Ordinal) { [name] = text },
            });
            return name;
        }

        /// <summary>Template for a value position: path token, literal, or a hoisted scalar for complex arithmetic.</summary>
        private object? Template(Expression node)
        {
            if (IsPathNode(node)) return "{{" + Path(node) + "}}";
            if (node is Literal { Value: double number } && double.IsFinite(number)) return number;
            if (node is Literal { Value: bool flag }) return flag;
            if (node is Literal { Value: string text }) return PlainText(node, node, text);
            if (node is BinaryExpression or UnaryExpression or CallExpression)
                return "{{" + Hoist(node) + "}}";
            throw Fail("EXPRESSION_UNSUPPORTED", "Unsupported value expression.", node);
        }

        private string Arithmetic(Expression node)
        {
            switch (node)
            {
                case Literal { Value: double number }:
                {
                    if (!double.IsFinite(number))
                        throw Fail("NON_FINITE_LITERAL", "Numeric literals must be finite.", node);
                    var text = number.ToString("R", CultureInfo.InvariantCulture);
                    if (text.Contains('E', StringComparison.Ordinal))
                        throw Fail("NUMBER_FORMAT_UNSUPPORTED", "Use plain decimal literals (no exponent form).", node);
                    return text;
                }
                case Identifier or MemberExpression:
                    return "{{" + Path(node) + "}}";
                case BinaryExpression binary:
                {
                    var op = JsAst.OperatorText(binary.Operator);
                    if (op is not ("+" or "-" or "*" or "/"))
                        throw Fail("EXPRESSION_UNSUPPORTED", $"Operator {op ?? "?"} is not supported in arithmetic.", binary);
                    _ops++;
                    return "(" + Arithmetic(binary.Left) + " " + op + " " + Arithmetic(binary.Right) + ")";
                }
                case UnaryExpression unary when JsAst.OperatorText(unary.Operator) == "-":
                    _ops++;
                    return "(0 - " + Arithmetic(unary.Argument) + ")";
                case CallExpression call when call.Callee is Identifier { Name: "floor" or "abs" } function:
                {
                    var args = call.Arguments.ToArray();
                    if (args.Length != 1)
                        throw Fail("EXPRESSION_UNSUPPORTED", $"{function.Name} takes one argument.", call);
                    _ops++;
                    return function.Name + "(" + Arithmetic(args[0]) + ")";
                }
                default:
                    throw Fail("EXPRESSION_UNSUPPORTED",
                        "Only numbers, paths, + - * /, unary -, floor and abs are allowed in arithmetic.", node);
            }
        }

        /// <summary>Dotted path rooted at a declared name. The root <c>now</c> maps to the captured clock.</summary>
        private string Path(Expression node)
        {
            var parts = new List<string>();
            Expression current = node;
            while (current is MemberExpression member)
            {
                if (member.Computed || member.Property is not Identifier property)
                    throw Fail("COMPUTED_MEMBER", "Computed member access is not supported; use a named property.", member);
                CheckMemberName(property.Name, member);
                parts.Insert(0, property.Name);
                current = member.Object;
            }

            if (current is not Identifier root)
                throw Fail("EXPRESSION_UNSUPPORTED", "A path must start with a name.", current);
            if (FunctionNames.Contains(root.Name))
                throw Fail("EXPRESSION_UNSUPPORTED", $"\"{root.Name}\" is a function and cannot be used as a value.", root);
            if (!_scope.Contains(root.Name))
                throw Fail("UNDECLARED_IDENTIFIER", $"\"{root.Name}\" is not declared.", root);
            if (root.Name == "now" && parts.Count > 0)
                throw Fail("EXPRESSION_UNSUPPORTED", "now is a timestamp and has no members.", node);
            if (root.Name == "values" && parts.Count == 0)
                throw Fail("EXPRESSION_UNSUPPORTED", "values must be read by member, for example values.combat.zombie_hp_per_level.", node);

            var head = root.Name == "now" ? "_unixMs" : root.Name;
            return parts.Count == 0 ? head : head + "." + string.Join('.', parts);
        }

        private void CheckMemberName(string name, Node node)
        {
            if (name is "__proto__" or "constructor" or "prototype" || name.StartsWith("__", StringComparison.Ordinal))
                throw Fail("PROTOTYPE_ACCESS", $"\"{name}\" is not allowed.", node);
        }

        private string Key(Expression node)
        {
            if (node is Literal { Value: string text })
                return PlainText(node, node, text);
            if (IsPathNode(node))
                return "{{" + Path(node) + "}}";
            throw Fail("EXPRESSION_UNSUPPORTED", "A record key must be steamId, a path, or a string literal.", node);
        }

        private string CollectionName(Expression node)
        {
            if (node is not Literal { Value: string name } || !IsIdentifierName(name))
                throw Fail("COLLECTION_NAME", "The collection must be a plain name string literal.", node);
            return name;
        }

        private string PlainText(Expression node, Node span, string? preset = null)
        {
            var text = preset ?? (node is Literal { Value: string literal } ? literal : null)
                ?? throw Fail("EXPRESSION_UNSUPPORTED", "Expected a string literal.", span);
            if (HasTemplateChars(text))
                throw Fail("LITERAL_TEMPLATE_CHARS", "String literals cannot contain {{ or }}.", span);
            return text;
        }

        private double StatusLiteral(Expression node, Node span)
        {
            if (node is Literal { Value: double status } && status is >= 100 and <= 599 && status == Math.Floor(status))
                return status;
            throw Fail("STATUS_LITERAL", "Status must be an integer literal from 100 to 599.", span);
        }

        private static bool HasTemplateChars(string text)
            => text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal);

        private static bool IsIdentifierName(string name)
        {
            if (name.Length == 0 || name.Length > 64) return false;
            if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
            return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        }

        private static bool IsPathNode(Expression node) => node is Identifier or MemberExpression;

        private static bool IsLiteral(Expression node) => node is Literal { Value: string or double or bool };

        private static bool IsCall(CallExpression call, string name)
            => call.Callee is Identifier callee && callee.Name == name;

        private static bool IsValuesFind(CallExpression call)
            => call.Callee is MemberExpression
            {
                Computed: false,
                Object: Identifier { Name: "values" },
                Property: Identifier { Name: "find" },
            };

        private static string Flip(string op) => op switch
        {
            "<" => ">",
            "<=" => ">=",
            ">" => "<",
            ">=" => "<=",
            _ => op,
        };

        private void AddStep(string id, Node node, Dictionary<string, object?> step)
        {
            if (_steps.Count >= NsLimits.MaxLoweredSteps)
                throw Fail("STEP_LIMIT_EXCEEDED", $"More than {NsLimits.MaxLoweredSteps} lowered steps.", node);
            _steps.Add(step);
            _sourceMap[id] = Span(node);
        }

        private static SourceSpan Span(Node node)
        {
            var (line, column) = JsAst.Span(node);
            return new SourceSpan(line, column);
        }

        private static NsCompileException Unsupported(Node node)
            => Fail("UNSUPPORTED_SYNTAX", $"Unsupported syntax {node.GetType().Name} in ns-endpoint/1.", node);

        /// <summary>Object entries as plain key/value pairs. Keys are validated against the name rules.</summary>
        private static IEnumerable<(string Key, Expression Value, Node Node)> Entries(ObjectExpression obj, bool requireIdentifierKeys)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in obj.Properties)
            {
                if (item is not Property { Computed: false, Value: Expression value } property)
                    throw Fail("UNSUPPORTED_SYNTAX", "Object entries must be plain key: value pairs.", item);

                string name = property.Key switch
                {
                    Identifier identifier => identifier.Name,
                    Literal { Value: string text } => text,
                    _ => throw Fail("UNSUPPORTED_SYNTAX", "Object keys must be names or string literals.", property),
                };

                if (name is "__proto__" or "constructor" or "prototype" || name.StartsWith("__", StringComparison.Ordinal))
                    throw Fail("PROTOTYPE_ACCESS", $"\"{name}\" is not allowed.", property);
                if (requireIdentifierKeys && !IsIdentifierName(name))
                    throw Fail("INVALID_KEY", $"\"{name}\" is not a plain name.", property);
                if (!seen.Add(name))
                    throw Fail("DUPLICATE_KEY", $"Duplicate key \"{name}\".", property);

                yield return (name, value, property);
            }
        }
    }
}
