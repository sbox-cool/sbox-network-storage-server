using System.Text.Json;
using System.Text.RegularExpressions;

namespace SboxNetworkStorage.Application.NetworkStorage.Media;

/// <summary>Content-addressed blob bytes per project. Metadata and grants live in <see cref="SboxNetworkStorage.Storage.IMediaStore"/>.</summary>
public interface IMediaBlobStore
{
    Task<bool> ExistsAsync(string projectId, string sha256, CancellationToken ct);
    Task WriteAsync(string projectId, string sha256, byte[] bytes, CancellationToken ct);
    /// <summary>
    /// Opens the blob for streaming, or returns null when it is missing. The caller disposes the result. Bytes are
    /// never buffered here: the endpoint copies the stream straight to the response.
    /// </summary>
    Task<MediaBlob?> OpenReadAsync(string projectId, string sha256, CancellationToken ct);
}

/// <summary>An open blob: a readable stream and its exact length. Disposing releases the file or HTTP response behind it.</summary>
public sealed class MediaBlob(Stream content, long length, IDisposable? owner) : IDisposable
{
    public Stream Content { get; } = content;
    public long Length { get; } = length;

    public void Dispose()
    {
        Content.Dispose();
        owner?.Dispose();
    }
}

/// <summary>Typed outcome of a media operation; the endpoint layer maps it to HTTP.</summary>
public sealed record MediaFailure(int Status, string Code, string Message);

/// <summary>Thrown by the save path when a purpose is refused. The owner page shows <see cref="Failure"/>'s message.</summary>
public sealed class MediaPolicyValidationException(MediaFailure failure) : Exception(failure.Message)
{
    public MediaFailure Failure { get; } = failure;
}

public sealed record MediaResult<T>(T? Value, MediaFailure? Failure)
{
    public bool Ok => Failure is null;
    public static MediaResult<T> Success(T value) => new(value, null);
    public static MediaResult<T> Fail(int status, string code, string message) => new(default, new MediaFailure(status, code, message));
}

public sealed record MediaPurposePolicy(
    string Name,
    bool Enabled,
    bool AllowClientSelfGrant,
    string Visibility,
    IReadOnlyList<string> ContentTypes,
    long MaxBytes,
    int MaxWidth,
    int MaxHeight,
    int PerPlayerPerHour,
    long ProjectQuotaBytes,
    int RetentionDays);

public sealed record MediaProjectPolicy(bool Enabled, IReadOnlyDictionary<string, MediaPurposePolicy> Purposes)
{
    public static MediaProjectPolicy Disabled { get; } = new(false, new Dictionary<string, MediaPurposePolicy>());
}

/// <summary>Server-wide media limits from configuration. Per-project policy can only tighten these.</summary>
public sealed record MediaServerOptions(long MaxObjectBytes);

/// <summary>
/// Parses the project's <c>media</c> object (<c>{ enabled, purposes: { name: { ... } } }</c>).
/// Missing or malformed values fall back to the safe default (off / private / restrictive limits).
/// </summary>
public static class MediaPolicyParser
{
    public const string VisibilityPublic = "public";
    public const string VisibilityPrivate = "private";
    public const int DefaultMaxWidth = 4096;
    public const int DefaultMaxHeight = 4096;
    public const int DefaultPerPlayerPerHour = 30;
    public const int DefaultRetentionDays = 30;
    public const int MaxRetentionDays = 3650;

    private static readonly Regex PurposeName = new("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<string> AllContentTypes = MediaSniffer.SupportedContentTypes;

    /// <summary>Parses the stored object. <paramref name="hardMaxBytes"/> is the server-wide cap every purpose is clamped to.</summary>
    public static MediaProjectPolicy Parse(JsonElement? media, long hardMaxBytes)
    {
        if (media is not { ValueKind: JsonValueKind.Object } root) return MediaProjectPolicy.Disabled;
        var enabled = ReadBool(root, "enabled", false);
        var purposes = new Dictionary<string, MediaPurposePolicy>(StringComparer.Ordinal);
        if (root.TryGetProperty("purposes", out var list) && list.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in list.EnumerateObject())
            {
                if (!PurposeName.IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                purposes[entry.Name] = ParsePurpose(entry.Name, entry.Value, hardMaxBytes);
            }
        }
        return new MediaProjectPolicy(enabled, purposes);
    }

    /// <summary>True when the purpose name is safe to store and use as a key (lowercase letters, digits, hyphen).</summary>
    public static bool IsValidPurposeName(string? name) => name is not null && PurposeName.IsMatch(name);

    /// <summary>
    /// Save-time rule for one purpose: client self-grant needs a finite project quota, otherwise any player holding a
    /// public key could fill the project's storage. Returns null when the purpose may be saved.
    /// </summary>
    public static MediaFailure? ValidateForSave(string purpose, bool allowClientSelfGrant, long projectQuotaBytes) =>
        allowClientSelfGrant && projectQuotaBytes <= 0
            ? new MediaFailure(422, "MEDIA_QUOTA_REQUIRED",
                $"Purpose '{purpose}' allows client self-grant, so it needs a project quota in bytes. Set a quota, or turn client self-grant off.")
            : null;

    private static MediaPurposePolicy ParsePurpose(string name, JsonElement p, long hardMaxBytes)
    {
        var visibility = ReadString(p, "visibility", VisibilityPrivate);
        if (visibility != VisibilityPublic) visibility = VisibilityPrivate;

        var types = new List<string>();
        if (p.TryGetProperty("contentTypes", out var typeList) && typeList.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typeList.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.String) continue;
                var value = t.GetString();
                if (value is not null && AllContentTypes.Contains(value) && !types.Contains(value)) types.Add(value);
            }
        }
        if (types.Count == 0) types.AddRange(AllContentTypes);

        return new MediaPurposePolicy(
            Name: name,
            Enabled: ReadBool(p, "enabled", false),
            AllowClientSelfGrant: ReadBool(p, "allowClientSelfGrant", false),
            Visibility: visibility,
            ContentTypes: types,
            MaxBytes: Math.Clamp(ReadLong(p, "maxBytes", hardMaxBytes), 1, hardMaxBytes),
            MaxWidth: (int)Math.Clamp(ReadLong(p, "maxWidth", DefaultMaxWidth), 1, 16384),
            MaxHeight: (int)Math.Clamp(ReadLong(p, "maxHeight", DefaultMaxHeight), 1, 16384),
            PerPlayerPerHour: (int)Math.Clamp(ReadLong(p, "perPlayerPerHour", DefaultPerPlayerPerHour), 1, 100_000),
            ProjectQuotaBytes: Math.Max(0, ReadLong(p, "projectQuotaBytes", 0)),
            RetentionDays: (int)Math.Clamp(ReadLong(p, "retentionDays", DefaultRetentionDays), 1, MaxRetentionDays));
    }

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static string ReadString(JsonElement obj, string name, string fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static long ReadLong(JsonElement obj, string name, long fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;
}
