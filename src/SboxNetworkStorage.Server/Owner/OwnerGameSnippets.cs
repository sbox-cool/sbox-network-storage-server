using System.Globalization;
using System.Text.Json;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// C# for calling a stored resource from s&amp;box game code, shared by the dashboard resource editor and the
/// coding-agent backend (<c>sbox-ns dev</c>), so both show the same call.
/// </summary>
internal static class OwnerGameSnippets
{
    /// <summary>
    /// <c>NetworkStorage.CallEndpoint</c> with an input object built from the endpoint's <c>input</c> schema,
    /// followed by the <c>TryGetLastEndpointError</c> check.
    /// </summary>
    public static string EndpointCall(string slug, JsonElement? definition)
    {
        var input = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("input", out var schema)
            ? OwnerProjectScope.SchemaSkeleton(schema)
            : [];
        var args = input.Count == 0 ? "" : ", " + ObjectLiteral(input);
        var literal = JsonSerializer.Serialize(slug);
        return $$"""
            var result = await NetworkStorage.CallEndpoint( {{literal}}{{args}} );
            if ( !result.HasValue )
            {
                NetworkStorage.TryGetLastEndpointError( {{literal}}, out var code, out var message );
                Log.Warning( $"{{slug}} failed: {code} {message}" );
                return;
            }
            """;
    }

    public static bool RequiresSecretKey(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("requiresSecretKey", out var secret)
            && secret.ValueKind == JsonValueKind.True;

    /// <summary>True when the collection declares <c>accessMode: public</c>, the only mode game clients may read and write directly.</summary>
    public static bool IsPublicCollection(JsonElement? definition)
        => definition is { ValueKind: JsonValueKind.Object } d && OwnerProjectScope.Text(d, "accessMode") == "public";

    /// <summary>
    /// <c>GetDocument</c> and <c>SaveDocument</c> for a public collection: the player's own document for per-player
    /// collections, a named record for global ones. The saved object comes from the collection schema.
    /// </summary>
    public static string CollectionDocuments(string collectionId, bool global, JsonElement? definition)
    {
        var fields = definition is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("schema", out var schema)
            ? OwnerProjectScope.SchemaSkeleton(schema)
            : [];
        var literal = JsonSerializer.Serialize(collectionId);
        var document = global ? "var doc = \"main\"; // the global record ID" : "var doc = Game.SteamId.ToString();";
        return $$"""
            {{document}}
            var data = await NetworkStorage.GetDocument( {{literal}}, doc );
            await NetworkStorage.SaveDocument( {{literal}}, doc, {{ObjectLiteral(fields)}} );
            """;
    }

    private static string ObjectLiteral(IReadOnlyDictionary<string, object?> values)
    {
        if (values.Count == 0) return "new { }";
        if (values.Keys.All(IsIdentifier))
            return "new { " + string.Join(", ", values.Select(pair => $"{pair.Key} = {Literal(pair.Value)}")) + " }";
        // Property names that are not C# identifiers need a dictionary.
        return "new Dictionary<string, object> { " + string.Join(", ", values.Select(pair => $"[{JsonSerializer.Serialize(pair.Key)}] = {Literal(pair.Value)}")) + " }";
    }

    private static string Literal(object? value) => value switch
    {
        null => "(object)null",
        string s => JsonSerializer.Serialize(s),
        bool b => b ? "true" : "false",
        JsonElement { ValueKind: JsonValueKind.String } e => JsonSerializer.Serialize(e.GetString()),
        JsonElement { ValueKind: JsonValueKind.True } => "true",
        JsonElement { ValueKind: JsonValueKind.False } => "false",
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetRawText(),
        JsonElement { ValueKind: JsonValueKind.Array } or Array => "new object[0]",
        JsonElement { ValueKind: JsonValueKind.Object } or IDictionary<string, object?> => "new { }",
        JsonElement => "(object)null",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "(object)null",
    };

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterOrDigit(c) && c != '_') return false;
        return !Keywords.Contains(name);
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };
}
