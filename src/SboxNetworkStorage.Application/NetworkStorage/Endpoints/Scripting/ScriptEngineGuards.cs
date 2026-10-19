using System.Globalization;
using Jint;

namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints.Scripting;

/// <summary>
/// Hardening applied to every Jint engine the server creates: endpoint modules, the global module loader and the
/// TypeScript stripper. It removes shared-memory and binary-buffer globals, caps single-call allocators, turns native
/// stack exhaustion into a catchable error and bounds regex and promise waits by the script timeout.
/// </summary>
internal static class ScriptEngineGuards
{
    /// <summary>Largest array length a script may create (applies to <c>new Array(n)</c>, <c>Array.from</c> and growth).</summary>
    public const uint MaxArrayLength = 1_000_000;

    /// <summary>Largest string a single built-in call may produce (<c>repeat</c>, <c>padStart</c>, <c>padEnd</c>, <c>join</c>).</summary>
    public const int MaxStringLength = 1_048_576;

    private const string HardeningSource = """
        (function () {
          ["SharedArrayBuffer", "Atomics"].forEach(function (name) { delete globalThis[name]; });
          if (__REMOVE_BINARY__) {
            ["ArrayBuffer", "DataView", "Int8Array", "Uint8Array", "Uint8ClampedArray", "Int16Array", "Uint16Array",
             "Int32Array", "Uint32Array", "Float16Array", "Float32Array", "Float64Array",
             "BigInt64Array", "BigUint64Array"].forEach(function (name) { delete globalThis[name]; });
          }

          var maxArray = __MAX_ARRAY__;
          var maxString = __MAX_STRING__;

          function define(target, name, fn) {
            Object.defineProperty(target, name, { value: fn, writable: true, configurable: true, enumerable: false });
          }
          function checkString(length, what) {
            if (length > maxString) throw new RangeError(what + " would exceed the string size limit");
          }

          var originalRepeat = String.prototype.repeat;
          define(String.prototype, "repeat", function repeat(count) {
            var s = String(this);
            var n = Number(count);
            if (s.length === 0 || !(n > 0)) {
              return originalRepeat.call(s, count);
            }
            checkString(s.length * n, "String.prototype.repeat");
            return originalRepeat.call(s, count);
          });

          function wrapPad(original, name) {
            return function (maxLength, fillString) {
              var s = String(this);
              var target = Number(maxLength);
              var fill = fillString === undefined ? " " : String(fillString);
              if (fill.length > 0 && target > s.length) checkString(target, name);
              return original.call(s, maxLength, fillString);
            };
          }
          define(String.prototype, "padStart", wrapPad(String.prototype.padStart, "String.prototype.padStart"));
          define(String.prototype, "padEnd", wrapPad(String.prototype.padEnd, "String.prototype.padEnd"));

          var originalFrom = Array.from;
          define(Array, "from", function from(items) {
            if (items !== null && items !== undefined && Number(items.length) > maxArray) {
              throw new RangeError("Array.from would exceed the array size limit");
            }
            return originalFrom.apply(this, arguments);
          });

          var originalJoin = Array.prototype.join;
          define(Array.prototype, "join", function join(separator) {
            var length = this.length;
            if (length > maxArray) throw new RangeError("Array.prototype.join would exceed the array size limit");
            // Primitive and string elements are sized exactly without running user code; objects are stringified by
            // their own toString and are bounded by the memory budget.
            var sep = separator === undefined ? "," : String(separator);
            var total = length > 0 ? (length - 1) * sep.length : 0;
            for (var i = 0; i < length; i++) {
              var v = this[i];
              if (typeof v === "string") total += v.length;
              else if (typeof v === "number" || typeof v === "boolean" || typeof v === "bigint") total += String(v).length;
            }
            checkString(total, "Array.prototype.join");
            return originalJoin.call(this, separator);
          });
        })();
        """;

    private static readonly Lazy<Prepared<Acornima.Ast.Script>> EndpointHardeningScript = new(() =>
        PrepareHardening(removeBinaryBuffers: true));

    private static readonly Lazy<Prepared<Acornima.Ast.Script>> StripperHardeningScript = new(() =>
        PrepareHardening(removeBinaryBuffers: false));

    private static Prepared<Acornima.Ast.Script> PrepareHardening(bool removeBinaryBuffers) =>
        Engine.PrepareScript(
            HardeningSource
                .Replace("__REMOVE_BINARY__", removeBinaryBuffers ? "true" : "false", StringComparison.Ordinal)
                .Replace("__MAX_ARRAY__", MaxArrayLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                .Replace("__MAX_STRING__", MaxStringLength.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            "script-guards.js");

    /// <summary>Sets the engine options every script engine needs. Call before constructing the engine.</summary>
    public static void Configure(Options options, TimeSpan timeout)
    {
        options.AgentCanSuspend = false;
        options.Constraints.StackOverflowGuard = true;
        options.Constraints.MaxArraySize = MaxArrayLength;
        options.Constraints.RegexTimeout = timeout;
        options.Constraints.PromiseTimeout = timeout;
    }

    /// <summary>
    /// Runs the hardening script on a freshly constructed engine, before any user or prelude code. Endpoint engines
    /// lose the binary-buffer globals; the TypeScript stripper keeps them because its bundled code builds typed arrays.
    /// </summary>
    public static void Apply(Engine engine, bool keepBinaryBuffers) =>
        engine.Execute(keepBinaryBuffers ? StripperHardeningScript.Value : EndpointHardeningScript.Value);

    /// <summary>Module preparation options whose regex timeout matches the script timeout.</summary>
    public static ModulePreparationOptions ModulePreparation(TimeSpan timeout) =>
        new() { ParsingOptions = new ModuleParsingOptions { RegexTimeout = timeout } };

    /// <summary>Script preparation options whose regex timeout matches the script timeout.</summary>
    public static ScriptPreparationOptions ScriptPreparation(TimeSpan timeout) =>
        new() { ParsingOptions = new ScriptParsingOptions { RegexTimeout = timeout } };
}
