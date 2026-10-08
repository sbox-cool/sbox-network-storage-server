using System.Text.Json;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Dumps every enumerable resource of the configured store into JSON-lines files,
/// reading only through <see cref="INetworkStorageStore"/> so SQLite and PostgreSQL
/// produce identical dumps. Rows are written as the store returns them (snake_case
/// columns); child rows carry their parent keys (<c>collection_id</c>, <c>record_key</c>,
/// <c>steam_id</c>). Work is batched per collection / player, never per database.
/// </summary>
public sealed class StoreDumpWriter(INetworkStorageStore store, string stagingRoot)
{
    private const string UsersDirectory = "network-storage/users";
    private readonly List<string> _entries = [];

    /// <summary>Archive entry names written so far, in write order.</summary>
    public IReadOnlyList<string> Entries => _entries;

    public async Task<(long WorkspaceObjects, long Memberships, IReadOnlyList<ExportedProject> Projects)> WriteAsync(CancellationToken ct)
    {
        var userIds = new SortedSet<string>(StringComparer.Ordinal)
        {
            NetworkStorageServices.LocalOwnerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var projectIds = new SortedSet<string>(StringComparer.Ordinal);

        long workspaceObjects;
        await using (var file = Open(ExportFormat.WorkspaceObjectsEntry))
        {
            workspaceObjects = await DumpWorkspaceDirectoryAsync(file, string.Empty, userIds, projectIds, ct);
        }

        long memberships = 0;
        await using (var file = Open(ExportFormat.MembershipsEntry))
        {
            foreach (var userId in userIds)
            {
                foreach (var row in await store.ListProjectsForUserAsync(userId, ct))
                {
                    file.Write(row, ("user_id", userId));
                    memberships++;
                    if (Text(row, "project_id") is { } projectId && ExportFormat.IsValidProjectId(projectId))
                    {
                        projectIds.Add(projectId);
                    }
                }
            }
        }

        var projects = new List<ExportedProject>();
        foreach (var projectId in projectIds)
        {
            projects.Add(new ExportedProject(projectId, await DumpProjectAsync(projectId, ct)));
        }

        return (workspaceObjects, memberships, projects);
    }

    /// <summary>Exports one project's objects without other projects or server configuration.</summary>
    public async Task<(long WorkspaceObjects, long Memberships, IReadOnlyList<ExportedProject> Projects)> WriteProjectAsync(
        string projectId, CancellationToken ct)
    {
        if (!ExportFormat.IsValidProjectId(projectId) || await store.ReadProjectAsync(projectId, ct) is null)
            throw new ExportArchiveException("Project not found.");

        var owner = NetworkStorageServices.LocalOwnerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        long objects;
        await using (var file = Open(ExportFormat.WorkspaceObjectsEntry))
        {
            objects = await DumpWorkspaceDirectoryAsync(file, $"{UsersDirectory}/{owner}/{projectId}",
                new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal), ct);
        }
        await using (var file = Open(ExportFormat.MembershipsEntry))
        {
            file.WriteObject(writer =>
            {
                writer.WriteString("user_id", owner);
                writer.WriteString("project_id", projectId);
                writer.WriteString("role", "owner");
                writer.WriteNumber("created_at_unix_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            });
        }
        return (objects, 1, [new ExportedProject(projectId, await DumpProjectAsync(projectId, ct))]);
    }

    private async Task<long> DumpWorkspaceDirectoryAsync(JsonLinesFile file, string directory, SortedSet<string> userIds,
        SortedSet<string> projectIds, CancellationToken ct)
    {
        long count = 0;
        foreach (var entry in await store.ListWorkspaceObjectsAsync(directory, ct))
        {
            var path = directory.Length == 0 ? entry.Name : $"{directory}/{entry.Name}";
            if (entry.IsDirectory)
            {
                if (directory == UsersDirectory && long.TryParse(entry.Name, out _))
                {
                    userIds.Add(entry.Name);
                }

                count += await DumpWorkspaceDirectoryAsync(file, path, userIds, projectIds, ct);
                continue;
            }

            if (await store.ReadWorkspaceObjectAsync(path, ct) is not { } content)
            {
                continue;
            }

            file.WriteObject(writer =>
            {
                writer.WriteString("path", path);
                writer.WriteString("content", content);
            });
            count++;
            if (directory.StartsWith(UsersDirectory + "/", StringComparison.Ordinal) && entry.Name == "projects.json")
            {
                AddProjectIds(content, projectIds);
            }
        }

        return count;
    }

    private static void AddProjectIds(string projectsJson, SortedSet<string> projectIds)
    {
        try
        {
            using var document = JsonDocument.Parse(projectsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var project in document.RootElement.EnumerateArray())
            {
                if (project.ValueKind == JsonValueKind.Object && Text(project, "id") is { } id && ExportFormat.IsValidProjectId(id))
                {
                    projectIds.Add(id);
                }
            }
        }
        catch (JsonException)
        {
            // An unreadable project list is still exported verbatim as a workspace object.
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> DumpProjectAsync(string projectId, CancellationToken ct)
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        if (await store.ReadProjectAsync(projectId, ct) is { } payload)
        {
            await WriteRowsAsync(projectId, "project", counts, file => file.WriteObject(w =>
            {
                w.WritePropertyName("payload");
                payload.WriteTo(w);
            }));
        }

        var collections = await store.ListCollectionsAsync(projectId, ct);
        await WriteListAsync(projectId, "collections", collections, counts);

        await using (var records = Open(ExportFormat.ProjectEntry(projectId, "records")))
        await using (var ledger = Open(ExportFormat.ProjectEntry(projectId, "ledger-entries")))
        await using (var globals = Open(ExportFormat.ProjectEntry(projectId, "global-records")))
        {
            foreach (var collectionId in collections.Select(c => Text(c, "collection_id")).OfType<string>())
            {
                foreach (var record in await store.ListRecordsAsync(projectId, collectionId, ct))
                {
                    records.Write(record, ("collection_id", collectionId));
                    if (Text(record, "record_key") is not { } recordKey)
                    {
                        continue;
                    }

                    foreach (var entry in await store.ListLedgerEntriesAsync(projectId, collectionId, recordKey, ct))
                    {
                        ledger.Write(entry, ("collection_id", collectionId), ("record_key", recordKey));
                    }
                }

                foreach (var global in await store.ListGlobalRecordsAsync(projectId, collectionId, ct))
                {
                    globals.Write(global, ("collection_id", collectionId));
                }
            }

            Count(records, "records", counts);
            Count(ledger, "ledger-entries", counts);
            Count(globals, "global-records", counts);
        }

        await WriteListAsync(projectId, "endpoints", await store.ListEndpointsAsync(projectId, ct), counts);
        await WriteListAsync(projectId, "workflows", await store.ListWorkflowsAsync(projectId, ct), counts);
        await WriteListAsync(projectId, "queries", await store.ListQueriesAsync(projectId, ct), counts);
        await WriteListAsync(projectId, "api-keys", await store.ListApiKeysAsync(projectId, ct), counts);
        await WriteListAsync(projectId, "pages", await store.ListPagesAsync(projectId, ct), counts);
        await WriteSingleAsync(projectId, "game-values", await store.ReadGameValuesAsync(projectId, ct), counts);
        await WriteSingleAsync(projectId, "rate-limits", await store.ReadRateLimitRulesAsync(projectId, ct), counts);
        await WriteSingleAsync(projectId, "checkpoint-cursor", await store.ReadCheckpointCursorAsync(projectId, ct), counts);

        var profiles = await store.ReadProjectProfilesAsync(projectId, ct);
        await WriteListAsync(projectId, "player-profiles", profiles, counts);
        await using (var sessions = Open(ExportFormat.ProjectEntry(projectId, "player-sessions")))
        await using (var events = Open(ExportFormat.ProjectEntry(projectId, "player-events")))
        {
            foreach (var profile in profiles)
            {
                if (Text(profile, "steam_id") is not { Length: > 0 } steamId)
                {
                    continue;
                }

                if (Text(profile, "current_session_id") is { Length: > 0 } sessionId
                    && await store.ReadPlayerSessionAsync(projectId, steamId, sessionId, ct) is { } session)
                {
                    sessions.Write(session, ("steam_id", steamId));
                }

                var eventCount = await store.CountPlayerEventsAsync(projectId, steamId, ct);
                if (eventCount > 0)
                {
                    var limit = (int)Math.Min(eventCount, int.MaxValue);
                    foreach (var playerEvent in await store.ListPlayerEventsAsync(projectId, steamId, long.MinValue, long.MaxValue, limit, ct))
                    {
                        events.Write(playerEvent);
                    }
                }
            }

            Count(sessions, "player-sessions", counts);
            Count(events, "player-events", counts);
        }

        await WriteListAsync(projectId, "audit-logs", await store.ListAuditLogsAsync(projectId, int.MaxValue, ct), counts);

        var storageBytes = await store.ReadProjectStorageBytesAsync(projectId, ct);
        if (storageBytes != 0)
        {
            await WriteRowsAsync(projectId, "usage", counts, file => file.WriteObject(w => w.WriteNumber("storage_bytes", storageBytes)));
        }

        return counts;
    }

    private Task WriteListAsync(string projectId, string resource, IReadOnlyList<JsonElement> rows, SortedDictionary<string, long> counts)
        => WriteRowsAsync(projectId, resource, counts, file =>
        {
            foreach (var row in rows)
            {
                file.Write(row);
            }
        });

    private Task WriteSingleAsync(string projectId, string resource, JsonElement? row, SortedDictionary<string, long> counts)
        => row is { } value ? WriteRowsAsync(projectId, resource, counts, file => file.Write(value)) : Task.CompletedTask;

    private async Task WriteRowsAsync(string projectId, string resource, SortedDictionary<string, long> counts, Action<JsonLinesFile> write)
    {
        await using var file = Open(ExportFormat.ProjectEntry(projectId, resource));
        write(file);
        Count(file, resource, counts);
    }

    private static void Count(JsonLinesFile file, string resource, SortedDictionary<string, long> counts)
    {
        if (file.Lines > 0)
        {
            counts[resource] = file.Lines;
        }
    }

    private JsonLinesFile Open(string entryName)
    {
        var file = new JsonLinesFile(stagingRoot, entryName, () => _entries.Add(entryName));
        return file;
    }

    internal static string? Text(JsonElement row, string name)
        => row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A JSON-lines staging file created on first write, so empty resources leave no entry.</summary>
    private sealed class JsonLinesFile(string stagingRoot, string entryName, Action onCreated) : IAsyncDisposable
    {
        private FileStream? _stream;
        private Utf8JsonWriter? _writer;

        public long Lines { get; private set; }

        public void Write(JsonElement row, params (string Name, string Value)[] parentKeys)
            => WriteObject(writer =>
            {
                foreach (var (name, value) in parentKeys)
                {
                    writer.WriteString(name, value);
                }

                if (row.ValueKind != JsonValueKind.Object)
                {
                    return;
                }

                foreach (var property in row.EnumerateObject())
                {
                    if (!parentKeys.Any(key => key.Name == property.Name))
                    {
                        property.WriteTo(writer);
                    }
                }
            });

        public void WriteObject(Action<Utf8JsonWriter> body)
        {
            if (_writer is null)
            {
                var path = Path.Combine(stagingRoot, entryName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
                _writer = new Utf8JsonWriter(_stream);
                onCreated();
            }

            _writer.WriteStartObject();
            body(_writer);
            _writer.WriteEndObject();
            _writer.Flush();
            _writer.Reset();
            _stream!.WriteByte((byte)'\n');
            Lines++;
        }

        public async ValueTask DisposeAsync()
        {
            if (_writer is not null)
            {
                await _writer.DisposeAsync();
            }

            if (_stream is not null)
            {
                await _stream.DisposeAsync();
            }
        }
    }
}
