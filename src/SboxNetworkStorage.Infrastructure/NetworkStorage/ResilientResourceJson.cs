using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Application.NetworkStorage;

namespace SboxNetworkStorage.Infrastructure.NetworkStorage;

/// <summary>
/// Lenient deserialization for Network Storage resource lists (collections.json,
/// endpoints.json).
///
/// These files are authored from several directions — the dashboard forms, the YAML
/// source compiler (<c>tools/sbox/source-compiler.js</c>, which copies any key the
/// author wrote, including explicit nulls), package sync, and the legacy server runtime.
/// The strict <see cref="JsonSerializer"/> path binds them to positional records whose
/// value-type members (<c>bool Enabled</c>, <c>int MaxRecords</c>, …) throw on an
/// explicit JSON null, and one bad element fails the WHOLE list.
///
/// That produced a confusing split-brain in the dashboard: the workspace project card
/// counts array elements with a tolerant <see cref="JsonDocument"/> parse and showed
/// "2 col · 2 end", while the project pages bound the same bytes strictly and rendered
/// nothing. Same file, two different answers.
///
/// This helper makes the typed read behave like the tolerant one: nulls on value types
/// fall back to the type default, and a single unparseable element is skipped rather
/// than discarding its siblings.
/// </summary>
internal static class ResilientResourceJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new NullTolerantValueConverter<bool>());
        options.Converters.Add(new NullTolerantValueConverter<int>());
        options.Converters.Add(new NullTolerantValueConverter<long>());
        options.Converters.Add(new NullTolerantValueConverter<double>());
        return options;
    }

    /// <summary>
    /// Deserializes a JSON array into <typeparamref name="T"/>, element by element.
    /// Returns an empty list for null/blank/non-array input. Elements that cannot be
    /// bound are skipped and reported through <paramref name="onElementError"/> rather
    /// than failing the whole read.
    /// </summary>
    internal static List<T> DeserializeList<T>(string? json, Action<int, Exception>? onElementError = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            onElementError?.Invoke(-1, ex);
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var results = new List<T>(document.RootElement.GetArrayLength());
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    var value = WithoutNullProperties(element).Deserialize<T>(Options);
                    if (value is not null) results.Add(value);
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
                {
                    onElementError?.Invoke(index, ex);
                }

                index++;
            }

            return Normalize(results);
        }
    }

    /// <summary>
    /// Fills in non-nullable string members that JSON left null.
    ///
    /// <see cref="CollectionResource"/> and <see cref="EndpointResource"/> declare
    /// members like <c>string CollectionType</c> as non-nullable, but nullable
    /// annotations are not enforced at runtime: System.Text.Json happily binds a
    /// missing or null property to null, and the compiler then lets callers write
    /// <c>collection.CollectionType.Length</c> with no warning. That is a
    /// NullReferenceException on the collection detail page for any collection whose
    /// stored JSON omits the field — which is exactly what a source-compiled
    /// collection looks like.
    ///
    /// Normalizing once here means every consumer (views, overview builder, analytics
    /// reader) gets the guarantee the type already claims to make.
    /// </summary>
    internal static List<T> Normalize<T>(List<T> items)
    {
        if (typeof(T) == typeof(CollectionResource))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var value = (CollectionResource)(object)items[i]!;
                items[i] = (T)(object)(value with
                {
                    Id = Or(value.Id, ""),
                    Name = Or(value.Name, Or(value.Id, "")),
                    // Matches the source compiler's default
                    // (tools/sbox/source-compiler.js: collectionType || "per-steamid")
                    // and the `?? "per-steamid"` fallbacks already scattered at call sites.
                    CollectionType = Or(value.CollectionType, "per-steamid"),
                });
            }
        }
        else if (typeof(T) == typeof(EndpointResource))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var value = (EndpointResource)(object)items[i]!;
                items[i] = (T)(object)(value with
                {
                    Id = Or(value.Id, ""),
                    Slug = Or(value.Slug, ""),
                    Name = Or(value.Name, Or(value.Slug, "")),
                    Method = Or(value.Method, "POST"),
                });
            }
        }

        return items;
    }

    /// <summary>
    /// Returns <paramref name="fallback"/> when <paramref name="value"/> is null or
    /// blank. Written with <see cref="string.IsNullOrWhiteSpace"/> rather than <c>??</c>
    /// because the members involved are declared non-nullable, so the compiler treats a
    /// null check on them as redundant even though JSON binding can produce null.
    /// </summary>
    private static string Or(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value;

    /// <summary>
    /// Drops top-level properties whose value is JSON null so the target record's own
    /// parameter default applies instead.
    ///
    /// This matters for more than just avoiding a throw: <c>CollectionResource</c>
    /// declares <c>int MaxRecords = 1</c>, and legacy server reads the same field as
    /// <c>collection.maxRecords || 1</c>. Binding <c>"maxRecords": null</c> to 0 would
    /// quietly cap a collection at zero records. "Key present but blank" means unset,
    /// so we make it genuinely absent and let the declared default win.
    /// </summary>
    private static JsonElement WithoutNullProperties(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element;

        var hasNull = false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null) { hasNull = true; break; }
        }

        if (!hasNull) return element;

        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                property.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        using var trimmed = JsonDocument.Parse(buffer.ToArray());
        return trimmed.RootElement.Clone();
    }

    /// <summary>
    /// Reads a JSON null as <c>default(T)</c> instead of throwing. Authored resource
    /// files routinely carry <c>"maxRecords": null</c> or <c>"enabled": null</c> for a
    /// YAML key left blank; those mean "unset", not "invalid".
    /// </summary>
    private sealed class NullTolerantValueConverter<T> : JsonConverter<T> where T : struct
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return default;

            // Accept the JSON-ish stringly-typed forms these files sometimes carry
            // ("true", "5") rather than rejecting the whole resource.
            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw)) return default;
                if (typeof(T) == typeof(bool) && bool.TryParse(raw, out var b)) return (T)(object)b;
                if (typeof(T) == typeof(int) && int.TryParse(raw, out var i)) return (T)(object)i;
                if (typeof(T) == typeof(long) && long.TryParse(raw, out var l)) return (T)(object)l;
                if (typeof(T) == typeof(double) && double.TryParse(raw, out var d)) return (T)(object)d;
                return default;
            }

            if (typeof(T) == typeof(bool))
            {
                if (reader.TokenType == JsonTokenType.True) return (T)(object)true;
                if (reader.TokenType == JsonTokenType.False) return (T)(object)false;
                // A number stands in for a flag in some legacy rows (0/1).
                if (reader.TokenType == JsonTokenType.Number) return (T)(object)(reader.GetDouble() != 0);
                return default;
            }

            if (reader.TokenType != JsonTokenType.Number) return default;

            if (typeof(T) == typeof(int)) return (T)(object)(int)reader.GetDouble();
            if (typeof(T) == typeof(long)) return (T)(object)(long)reader.GetDouble();
            if (typeof(T) == typeof(double)) return (T)(object)reader.GetDouble();

            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case bool b: writer.WriteBooleanValue(b); break;
                case int i: writer.WriteNumberValue(i); break;
                case long l: writer.WriteNumberValue(l); break;
                case double d: writer.WriteNumberValue(d); break;
                default: writer.WriteNullValue(); break;
            }
        }
    }
}
