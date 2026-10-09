using System.Data;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace SboxNetworkStorage.Storage.Relational;

public abstract partial class RelationalNetworkStorageStore : IAuthoritativeProjectStore
{
    public async IAsyncEnumerable<ProjectSnapshotRow> ExportProjectRowsAsync(string projectId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var table in ProjectSnapshotSchema.Tables)
        {
            var names = table.Value.Keys.Order(StringComparer.Ordinal).ToArray();
            await using var command = Command(connection,
                $"SELECT {string.Join(',', names)} FROM {TablePrefix}{table.Key} WHERE project_id = @project_id",
                [Text("project_id", projectId)], transaction);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                using var bytes = new MemoryStream();
                using (var json = new Utf8JsonWriter(bytes))
                {
                    json.WriteStartObject();
                    for (var i = 0; i < names.Length; i++)
                    {
                        json.WritePropertyName(names[i]);
                        if (reader.IsDBNull(i)) json.WriteNullValue();
                        else if (table.Value[names[i]] == JsonValueKind.String) json.WriteStringValue(reader.GetString(i));
                        else if (table.Value[names[i]] == JsonValueKind.True) json.WriteBooleanValue(Convert.ToBoolean(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                        else json.WriteNumberValue(Convert.ToInt64(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                    }
                    json.WriteEndObject();
                }
                using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
                yield return new ProjectSnapshotRow(table.Key, document.RootElement.Clone());
            }
        }
        await transaction.CommitAsync(ct);
    }

    public async Task ReplaceProjectRowsAsync(string projectId, IAsyncEnumerable<ProjectSnapshotRow> rows, CancellationToken ct)
    {
        StoreValidation.Id(projectId);
        if (_transaction is not null) throw new InvalidOperationException("Snapshot imports cannot run inside a transaction.");
        await using var connection = await OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        foreach (var table in ProjectSnapshotSchema.Tables.Keys)
        {
            await using var delete = Command(connection, $"DELETE FROM {TablePrefix}{table} WHERE project_id = @project_id",
                [Text("project_id", projectId)], transaction);
            await delete.ExecuteNonQueryAsync(ct);
        }
        await foreach (var snapshot in rows.WithCancellation(ct))
        {
            ProjectSnapshotSchema.Validate(projectId, snapshot.Table, snapshot.Row);
            var columns = ProjectSnapshotSchema.Tables[snapshot.Table];
            var args = columns.Select(column =>
            {
                var value = snapshot.Row.GetProperty(column.Key);
                if (column.Value == JsonValueKind.String) return Text(column.Key, value.ValueKind == JsonValueKind.Null ? null : value.GetString());
                if (column.Value == JsonValueKind.True) return Bool(column.Key, value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean());
                return Int64(column.Key, value.ValueKind == JsonValueKind.Null ? null : value.GetInt64());
            }).ToArray();
            await using var insert = Command(connection,
                $"INSERT INTO {TablePrefix}{snapshot.Table} ({string.Join(',', columns.Keys)}) VALUES ({string.Join(',', columns.Keys.Select(n => "@" + n))})",
                args, transaction);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }
}
