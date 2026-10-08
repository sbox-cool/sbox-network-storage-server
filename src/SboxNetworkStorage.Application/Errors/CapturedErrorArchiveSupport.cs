using System.Text.Json;
using System.Text.RegularExpressions;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Application.Errors;

public static partial class CapturedErrorArchiveSupport
{
    public const int MaxMessageLength = 8000;
    public const int MaxStackLength = 20000;
    public const int DiscordEmbedFieldValueLimit = 1010;
    public const int DiscordStackFieldLimit = 4;

    public static CapturedErrorDto NormalizeForStorage(CapturedErrorDto error)
    {
        var id = Guid.TryParse(error.Id, out var parsedId)
            ? parsedId.ToString("D")
            : Guid.NewGuid().ToString("D");

        return error with
        {
            Id = id,
            Message = Truncate(error.Message, MaxMessageLength),
            StackTrace = string.IsNullOrWhiteSpace(error.StackTrace)
                ? null
                : Truncate(error.StackTrace, MaxStackLength),
        };
    }

    public static CapturedErrorDto ForList(CapturedErrorDto error) => error with { StackTrace = null };

    public static string BuildFingerprint(CapturedErrorDto error)
    {
        var status = error.StatusCode.ToString();
        var message = error.Message ?? string.Empty;
        var messagePrefix = message.Length > 180 ? message[..180] : message;
        return string.Join("|", error.Source, error.Method, error.Path, status, messagePrefix);
    }

    public static string BuildPayloadJson(CapturedErrorDto error)
    {
        return JsonSerializer.Serialize(new
        {
            classification = error.Classification,
            correlationId = error.CorrelationId,
            archive = "dotnet",
        });
    }

    public static (string Classification, string CorrelationId) ParsePayload(string? payloadJson, string? requestId)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
        {
            try
            {
                using var document = JsonDocument.Parse(payloadJson);
                var root = document.RootElement;
                var classification = root.TryGetProperty("classification", out var classificationElement)
                    ? classificationElement.GetString()
                    : null;
                var correlationId = root.TryGetProperty("correlationId", out var correlationElement)
                    ? correlationElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(classification) || !string.IsNullOrWhiteSpace(correlationId))
                {
                    return (
                        string.IsNullOrWhiteSpace(classification) ? "Error" : classification,
                        string.IsNullOrWhiteSpace(correlationId) ? requestId ?? string.Empty : correlationId);
                }
            }
            catch (JsonException)
            {
            }
        }

        return ("Error", requestId ?? string.Empty);
    }

    public static string RedactSensitiveText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Redact specific high-signal secrets first. The generic assignment
        // pattern below is greedy on a single \S+ token, so running it first
        // would consume the "Bearer" keyword and orphan the actual token
        // (e.g. "Authorization: Bearer abc.def.ghi" -> "Authorization[REDACTED] abc.def.ghi"),
        // leaking the credential. Specific patterns must win before the catch-all.
        var redacted = AuthorizationHeaderPattern().Replace(value, "Authorization: [REDACTED]");
        redacted = BearerTokenPattern().Replace(redacted, "Bearer [REDACTED]");
        redacted = PostgresConnectionPattern().Replace(redacted, "postgres://[REDACTED]");
        redacted = SensitiveAssignmentPattern().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    public static IReadOnlyList<(string Name, string Value)> BuildDiscordStackFields(string? stackTrace)
    {
        if (string.IsNullOrWhiteSpace(stackTrace))
        {
            return [];
        }

        var sanitized = RedactSensitiveText(stackTrace);
        var fields = new List<(string Name, string Value)>();
        var lines = sanitized.Split('\n');
        var chunk = string.Empty;
        var chunkIndex = 1;

        foreach (var line in lines)
        {
            if (fields.Count >= DiscordStackFieldLimit)
            {
                break;
            }

            var candidate = string.IsNullOrEmpty(chunk) ? line : $"{chunk}\n{line}";
            if (candidate.Length > DiscordEmbedFieldValueLimit && !string.IsNullOrEmpty(chunk))
            {
                fields.Add((chunkIndex > 1 ? $"Stack ({chunkIndex})" : "Stack", WrapCodeBlock(chunk)));
                chunk = line;
                chunkIndex++;
                continue;
            }

            chunk = candidate;
        }

        if (!string.IsNullOrWhiteSpace(chunk) && fields.Count < DiscordStackFieldLimit)
        {
            fields.Add((chunkIndex > 1 ? $"Stack ({chunkIndex})" : "Stack", WrapCodeBlock(chunk.Trim())));
        }

        return fields;
    }

    public static string AdminDetailUrl(string publicBaseUrl, string errorId)
    {
        var baseUrl = string.IsNullOrWhiteSpace(publicBaseUrl) ? "https://sboxcool.com" : publicBaseUrl.TrimEnd('/');
        return $"{baseUrl}/admin/errors/{Uri.EscapeDataString(errorId)}";
    }


    private static readonly string[] RangeKeys = ["24h", "7d", "30d", "90d", "all"];

    private static DateTimeOffset? StartAtForRange(string range, DateTimeOffset now)
        => range switch
        {
            "24h" => now.AddDays(-1),
            "7d" => now.AddDays(-7),
            "30d" => now.AddDays(-30),
            "90d" => now.AddDays(-90),
            _ => null
        };

    public static string NormalizeInternalErrorRange(string? range)
    {
        var value = string.IsNullOrWhiteSpace(range) ? "all" : range.Trim().ToLowerInvariant();
        return RangeKeys.Contains(value) ? value : "all";
    }

    public static InternalErrorArchiveQuery NormalizeQuery(InternalErrorArchiveQuery query)
        => new(
            (query.Query ?? string.Empty).Trim(),
            (query.Source ?? string.Empty).Trim(),
            (query.ProjectId ?? string.Empty).Trim(),
            NormalizeInternalErrorRange(query.Range),
            Math.Clamp(query.Limit, 1, 200),
            Math.Max(0, query.Offset));

    public static bool Matches(CapturedErrorDto error, InternalErrorArchiveQuery query, DateTimeOffset now)
    {
        var normalized = NormalizeQuery(query);
        var startAt = StartAtForRange(normalized.Range, now);
        if (startAt.HasValue && error.Timestamp < startAt.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalized.Source)
            && !Contains(error.Source, normalized.Source))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(normalized.ProjectId)
            && !string.Equals(error.ProjectId, normalized.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Query.Length < 2)
        {
            return true;
        }

        return Contains(error.Id, normalized.Query)
            || Contains(error.Message, normalized.Query)
            || Contains(error.Path, normalized.Query)
            || Contains(error.Source, normalized.Query)
            || Contains(error.CorrelationId, normalized.Query)
            || Contains(error.Classification, normalized.Query)
            || Contains(error.StorageBackend, normalized.Query)
            || Contains(error.Fingerprint, normalized.Query)
            || Contains(error.ProjectId, normalized.Query);
    }

    public static string FormatPayloadText(string? payloadJson, int maxLength = 250_000)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var text = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
            return text.Length <= maxLength
                ? text
                : string.Concat(text.AsSpan(0, Math.Max(0, maxLength - 80)), "\n... payload truncated for admin detail rendering ...");
        }
        catch (JsonException)
        {
            return payloadJson.Length <= maxLength
                ? payloadJson
                : string.Concat(payloadJson.AsSpan(0, Math.Max(0, maxLength - 80)), "\n... payload truncated ...");
        }
    }

    private static bool Contains(string? value, string query)
        => !string.IsNullOrEmpty(value) && value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string WrapCodeBlock(string value)
    {
        var trimmed = Truncate(value, DiscordEmbedFieldValueLimit);
        return $"```\n{trimmed}\n```";
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - 3), "...");
    }

    [GeneratedRegex(@"(?i)(password|secret|token|api[_-]?key|authorization)\s*[:=]\s*\S+")]
    private static partial Regex SensitiveAssignmentPattern();

    [GeneratedRegex(@"(?i)Authorization:\s*Bearer\s+\S+")]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"(?i)\bBearer\s+\S+")]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"(?i)postgres(?:ql)?://\S+")]
    private static partial Regex PostgresConnectionPattern();
}
