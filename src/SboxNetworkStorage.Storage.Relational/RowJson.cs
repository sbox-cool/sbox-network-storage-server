using System.Buffers;
using System.Data.Common;
using System.Text;
using System.Text.Json;

namespace SboxNetworkStorage.Storage.Relational;

/// <summary>How a selected column is rendered into the row JSON object.</summary>
internal enum ColumnKind
{
    /// <summary>String or null.</summary>
    Text,
    /// <summary>Number; NULL renders as 0 (production reads these with non-nullable <c>GetValue&lt;long&gt;</c>).</summary>
    Long,
    /// <summary>Number or null.</summary>
    LongOrNull,
    /// <summary>Number; NULL renders as 0.</summary>
    Int,
    /// <summary>Number or null.</summary>
    IntOrNull,
    /// <summary>Boolean; NULL renders as false.</summary>
    Bool,
    /// <summary>Boolean or null.</summary>
    BoolOrNull,
    /// <summary>Text column holding JSON, rendered as the parsed value; empty or corrupt text renders as null.</summary>
    Json,
}

/// <summary>A selected column. <see cref="Expression"/> overrides the SQL select expression.</summary>
internal readonly record struct Column(string Name, ColumnKind Kind, string? Expression = null);

/// <summary>Renders database rows into detached <see cref="JsonElement"/> objects keyed by column name.</summary>
internal static class RowJson
{
    public static JsonElement Read(DbDataReader reader, Column[] columns)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i];
                writer.WritePropertyName(column.Name);
                var isNull = reader.IsDBNull(i);
                switch (column.Kind)
                {
                    case ColumnKind.Text:
                        if (isNull) writer.WriteNullValue(); else writer.WriteStringValue(reader.GetString(i));
                        break;
                    case ColumnKind.Long:
                        writer.WriteNumberValue(isNull ? 0L : reader.GetInt64(i));
                        break;
                    case ColumnKind.LongOrNull:
                        if (isNull) writer.WriteNullValue(); else writer.WriteNumberValue(reader.GetInt64(i));
                        break;
                    case ColumnKind.Int:
                        writer.WriteNumberValue(isNull ? 0 : reader.GetInt32(i));
                        break;
                    case ColumnKind.IntOrNull:
                        if (isNull) writer.WriteNullValue(); else writer.WriteNumberValue(reader.GetInt32(i));
                        break;
                    case ColumnKind.Bool:
                        writer.WriteBooleanValue(!isNull && reader.GetBoolean(i));
                        break;
                    case ColumnKind.BoolOrNull:
                        if (isNull) writer.WriteNullValue(); else writer.WriteBooleanValue(reader.GetBoolean(i));
                        break;
                    case ColumnKind.Json:
                        WriteJsonText(writer, isNull ? null : reader.GetString(i));
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown column kind {column.Kind}.");
                }
            }
            writer.WriteEndObject();
        }

        var jsonReader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref jsonReader);
    }

    /// <summary>
    /// Parses a stored JSON text column into a detached element. Null, empty,
    /// whitespace and corrupt values yield null, matching production's
    /// <c>ParseJsonColumn</c>.
    /// </summary>
    public static JsonElement? ParseJsonColumn(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteJsonText(Utf8JsonWriter writer, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            writer.WriteNullValue();
            return;
        }

        var utf8 = Encoding.UTF8.GetBytes(text);
        if (IsSingleJsonValue(utf8))
            writer.WriteRawValue(utf8, skipInputValidation: true);
        else
            writer.WriteNullValue();
    }

    private static bool IsSingleJsonValue(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read()) return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
