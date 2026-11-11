using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SboxNetworkStorage.Server.Telemetry;
using SboxNetworkStorage.Server.Tunnels;

namespace SboxNetworkStorage.Server.Backups;

public sealed record BackupPlan(string Id, string Label, long QuotaBytes, int Keep);

public sealed record BackupStatus(bool Enabled, string Status, string Message, BackupPlan? Plan, long UsedBytes, long BackupCount, DateTimeOffset? LastBackupAt);

public sealed record RemoteBackup(string BackupId, DateTimeOffset CreatedAt, long SizeBytes, string Sha256);

public sealed record UploadTicket(string BackupId, Uri UploadUrl, Dictionary<string, string>? UploadHeaders, DateTimeOffset ExpiresAt);

/// <summary>A refusal or unusable response from sboxns Backups. The message is fixed text; remote bodies are never echoed.</summary>
public sealed class BackupApiException(string code, string message, int? httpStatus = null) : Exception(message)
{
    public string Code { get; } = code;
    public int? HttpStatus { get; } = httpStatus;
}

/// <summary>
/// Signed client for the sboxns Backups API. Every request is signed with the install identity; nothing is sent
/// until a method is called.
/// </summary>
public sealed class SboxNsBackupClient : IDisposable
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly TunnelIdentity _identity;
    private readonly string _baseUrl;

    public SboxNsBackupClient(Uri endpoint, TunnelIdentity identity, HttpMessageHandler? handler = null)
    {
        _identity = identity;
        _baseUrl = endpoint.AbsoluteUri.TrimEnd('/');
        // Per-request timeouts are applied below; uploads can run much longer than the telemetry client's limit.
        _http = UsageTelemetry.CreateHttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public Task<BackupStatus> StatusAsync(CancellationToken ct)
        => PostAsync<BackupStatus>("status", Sign("status", string.Empty), ct);

    /// <summary>Reserves a presigned upload. The caller must PUT the bytes with <see cref="UploadAsync"/> and then call <see cref="CompleteAsync"/>.</summary>
    public async Task<UploadTicket> RequestUploadAsync(long sizeBytes, string sha256, CancellationToken ct)
    {
        var ticket = await PostAsync<UploadTicket>("upload",
            Sign("upload", $"{sizeBytes}:{sha256}", sizeBytes: sizeBytes, sha256: sha256), ct);
        RequireTransferUrl(ticket.UploadUrl);
        return ticket;
    }

    /// <summary>PUTs the object with exactly the headers sboxns returned and Content-Length set to the object size.</summary>
    public async Task UploadAsync(UploadTicket ticket, Stream content, long sizeBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ticket.UploadUrl);
        var body = new StreamContent(content);
        body.Headers.ContentLength = sizeBytes;
        foreach (var header in ticket.UploadHeaders ?? new Dictionary<string, string>())
        {
            if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                body.Headers.TryAddWithoutValidation(header.Key, header.Value);
            else
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Content = body;

        using var cts = Linked(ct, TransferTimeout);
        using var response = await _http.SendAsync(request, cts.Token);
        if (!response.IsSuccessStatusCode)
            throw new BackupApiException("UPLOAD_FAILED",
                $"Upload to sboxns storage failed (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
    }

    /// <summary>Asks sboxns to verify the uploaded object and record it as a completed backup.</summary>
    public async Task<RemoteBackup> CompleteAsync(string backupId, CancellationToken ct)
        => (await PostAsync<CompleteResponse>("complete", Sign("complete", backupId, backupId: backupId), ct)).Backup;

    /// <summary>Completed backups only, newest first.</summary>
    public async Task<IReadOnlyList<RemoteBackup>> ListAsync(CancellationToken ct)
        => (await PostAsync<ListResponse>("list", Sign("list", string.Empty), ct)).Backups ?? new List<RemoteBackup>();

    /// <summary>
    /// Downloads a backup to <paramref name="destination"/>. The bytes are written to a temporary file and moved into place only
    /// when the size and SHA-256 match the listing; otherwise the download is discarded.
    /// </summary>
    public async Task DownloadAsync(RemoteBackup backup, string destination, CancellationToken ct)
    {
        var link = await PostAsync<DownloadLink>("download", Sign("download", backup.BackupId, backupId: backup.BackupId), ct);
        RequireTransferUrl(link.DownloadUrl);
        var partial = destination + ".partial";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link.DownloadUrl);
            using var cts = Linked(ct, TransferTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new BackupApiException("DOWNLOAD_FAILED",
                    $"Download from sboxns storage failed (HTTP {(int)response.StatusCode}).", (int)response.StatusCode);
            await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(target, cts.Token);
            }

            var (size, sha256) = Digest(partial);
            if (size != backup.SizeBytes || !string.Equals(sha256, backup.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new BackupApiException("DOWNLOAD_MISMATCH",
                    "The downloaded backup does not match the size and checksum sboxns recorded. It was discarded.");
            File.Move(partial, destination, overwrite: false);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    public async Task DeleteAsync(string backupId, CancellationToken ct)
    {
        var response = await PostAsync<DeletedResponse>("delete", Sign("delete", backupId, backupId: backupId), ct);
        if (!response.Deleted)
            throw InvalidResponse();
    }

    private Envelope Sign(string action, string detail, long? sizeBytes = null, string? sha256 = null, string? backupId = null)
    {
        if (backupId is not null && !IsValidBackupId(backupId))
            throw new ArgumentException("Invalid backup ID.");
        var signed = _identity.SignBackup(action, detail);
        return new Envelope(signed.Name, signed.PublicKey, signed.Timestamp, signed.Nonce, signed.Signature, sizeBytes, sha256, backupId);
    }

    private static bool IsValidBackupId(string value)
        => value.Length is >= 16 and <= 40 && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');

    private static (long SizeBytes, string Sha256) Digest(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private async Task<T> PostAsync<T>(string action, Envelope body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/{action}")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        using var cts = Linked(ct, ApiTimeout);
        using var response = await _http.SendAsync(request, cts.Token);
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        if (!response.IsSuccessStatusCode)
            throw ApiError(response.StatusCode, text);
        try
        {
            return JsonSerializer.Deserialize<T>(text, Json) ?? throw InvalidResponse();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static BackupApiException ApiError(HttpStatusCode status, string body)
    {
        string? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var element) && element.ValueKind == JsonValueKind.String)
                code = element.GetString();
        }
        catch (JsonException)
        {
            // A non-JSON error body is reported by status code alone.
        }

        var message = code switch
        {
            "BACKUPS_NOT_ENABLED" => "sboxns Backups is not enabled for this install.",
            "QUOTA_EXCEEDED" => "This backup is larger than the sboxns Backups quota.",
            "INVALID_SIGNATURE" => "sboxns rejected the request signature. Check the install identity with: sbox-ns backup identity",
            "NONCE_REUSED" => "sboxns rejected a repeated request. Try again.",
            "UPLOAD_MISSING" => "The uploaded backup was not found in sboxns storage.",
            "SIZE_MISMATCH" => "The uploaded backup size did not match in sboxns storage.",
            "BACKUP_NOT_FOUND" => "That backup does not exist in sboxns Backups.",
            _ when status == HttpStatusCode.TooManyRequests => "sboxns Backups is rate limiting requests. Try again later.",
            _ => $"sboxns Backups returned HTTP {(int)status}.",
        };
        return new BackupApiException(code ?? $"HTTP_{(int)status}", message, (int)status);
    }

    private static BackupApiException InvalidResponse()
        => new("INVALID_RESPONSE", "sboxns Backups returned an unexpected response.");

    /// <summary>Presigned URLs must be HTTPS (HTTP only on loopback for tests) and carry no credentials.</summary>
    private static void RequireTransferUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || url.UserInfo.Length > 0)
            throw InvalidResponse();
        var loopback = url.Scheme == Uri.UriSchemeHttp
            && (url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(url.Host, out var address) && IPAddress.IsLoopback(address));
        if (url.Scheme != Uri.UriSchemeHttps && !loopback)
            throw InvalidResponse();
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    public void Dispose() => _http.Dispose();

    private sealed record Envelope(string Name, string PublicKey, long Timestamp, string Nonce, string Signature,
        long? SizeBytes, string? Sha256, string? BackupId);

    private sealed record CompleteResponse(RemoteBackup Backup);

    private sealed record ListResponse(List<RemoteBackup>? Backups);

    private sealed record DownloadLink(Uri DownloadUrl, DateTimeOffset ExpiresAt);

    private sealed record DeletedResponse(bool Deleted);
}
