using System.Data.Common;

namespace SboxNetworkStorage.Storage.Relational;

public abstract partial class RelationalNetworkStorageStore : IProjectSecretStore
{
    public async Task<IReadOnlyList<ProjectSecretSummary>> ListProjectSecretsAsync(string projectId, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            SELECT secret_name, updated_at_unix_ms FROM {TablePrefix}project_secrets
            WHERE project_id = @project ORDER BY secret_name
            """, [Text("project", projectId)]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<ProjectSecretSummary>();
        while (await reader.ReadAsync(ct))
            rows.Add(new ProjectSecretSummary(reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }

    public async Task<string?> GetProjectSecretSealedAsync(string projectId, string name, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        var (found, value) = await QueryTextAsync($"""
            SELECT value_enc FROM {TablePrefix}project_secrets
            WHERE project_id = @project AND secret_name = @name
            """, ct, Text("project", projectId), Text("name", name));
        return found ? value : null;
    }

    /// <summary>
    /// Replace-or-create under one transaction. The owner lock comes first (PostgreSQL takes an advisory lock), so the
    /// count below sees every row committed by a concurrent call. The UPDATE is the first write statement: SQLite takes
    /// its database write lock there and serialises the same way.
    /// </summary>
    public async Task<ProjectSecretWriteResult> SetProjectSecretAsync(string projectId, string name, string sealedValue, long updatedAtUnixMs, int maxSecrets, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        if (_transaction is { } bound)
            return await SetProjectSecretWithinAsync(bound.Connection!, bound, projectId, name, sealedValue, updatedAtUnixMs, maxSecrets, ct);

        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var result = await SetProjectSecretWithinAsync(connection, transaction, projectId, name, sealedValue, updatedAtUnixMs, maxSecrets, ct);
        if (result == ProjectSecretWriteResult.LimitReached)
        {
            await transaction.RollbackAsync(ct);
        }
        else
        {
            await transaction.CommitAsync(ct);
        }
        return result;
    }

    private async Task<ProjectSecretWriteResult> SetProjectSecretWithinAsync(DbConnection connection, DbTransaction transaction, string projectId, string name,
        string sealedValue, long updatedAtUnixMs, int maxSecrets, CancellationToken ct)
    {
        await LockProjectSecretsAsync(connection, transaction, projectId, ct);

        await using (var update = Command(connection, $"""
            UPDATE {TablePrefix}project_secrets SET value_enc = @value, updated_at_unix_ms = @updated
            WHERE project_id = @project AND secret_name = @name
            """,
            [Text("value", sealedValue), Int64("updated", updatedAtUnixMs), Text("project", projectId), Text("name", name)], transaction))
        {
            if (await update.ExecuteNonQueryAsync(ct) == 1) return ProjectSecretWriteResult.Replaced;
        }

        await using var insert = Command(connection, $"""
            INSERT INTO {TablePrefix}project_secrets (project_id, secret_name, value_enc, updated_at_unix_ms)
            SELECT @project, @name, @value, @updated
            WHERE (SELECT COUNT(*) FROM {TablePrefix}project_secrets WHERE project_id = @project) < @limit
            """,
            [Text("project", projectId), Text("name", name), Text("value", sealedValue), Int64("updated", updatedAtUnixMs), Int32("limit", maxSecrets)],
            transaction);
        return await insert.ExecuteNonQueryAsync(ct) == 1 ? ProjectSecretWriteResult.Created : ProjectSecretWriteResult.LimitReached;
    }

    public async Task<bool> DeleteProjectSecretAsync(string projectId, string name, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var lease = await LeaseAsync(ct);
        await using var command = Command(lease, $"""
            DELETE FROM {TablePrefix}project_secrets WHERE project_id = @project AND secret_name = @name
            """, [Text("project", projectId), Text("name", name)]);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Serialises writers for one project's secrets. The base store has no lock (SQLite's write lock already serialises
    /// writers); a provider with row-snapshot isolation (PostgreSQL) overrides it.
    /// </summary>
    protected virtual Task LockProjectSecretsAsync(DbConnection connection, DbTransaction transaction, string projectId, CancellationToken ct) =>
        Task.CompletedTask;
}
