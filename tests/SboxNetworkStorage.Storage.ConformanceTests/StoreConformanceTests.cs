using SboxNetworkStorage.Storage.Postgres;
using SboxNetworkStorage.Storage.Sqlite;

namespace SboxNetworkStorage.Storage.ConformanceTests;

/// <summary>
/// Behavioral contract every <see cref="INetworkStorageStore"/> driver must
/// satisfy; the expectations are the production the store store's behavior.
/// Split across partial files by table family.
/// </summary>
public abstract partial class StoreConformanceTests : IAsyncLifetime
{
    protected const long Start = 1_760_000_000_000;
    protected static readonly CancellationToken Ct = CancellationToken.None;

    protected ManualTimeProvider Time { get; } = new(Start);

    private INetworkStorageStore? _store;

    /// <summary>Creates a fresh, migrated, empty store. May call <c>Skip.If</c>.</summary>
    protected abstract Task<INetworkStorageStore> CreateStoreAsync(ManualTimeProvider time);

    protected virtual Task CleanupAsync(INetworkStorageStore store) => Task.CompletedTask;

    protected async Task<INetworkStorageStore> StoreAsync() => _store ??= await CreateStoreAsync(Time);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_store is not null) await CleanupAsync(_store);
    }
}

public sealed class InMemoryStoreConformanceTests : StoreConformanceTests
{
    protected override Task<INetworkStorageStore> CreateStoreAsync(ManualTimeProvider time)
        => Task.FromResult<INetworkStorageStore>(new InMemoryNetworkStorageStore(time));
}

public sealed class SqliteStoreConformanceTests : StoreConformanceTests
{
    private readonly string _path = TestDatabases.NewSqlitePath();

    protected override async Task<INetworkStorageStore> CreateStoreAsync(ManualTimeProvider time)
    {
        var store = TestDatabases.CreateSqlite(_path, time);
        await store.MigrateAsync(Ct);
        return store;
    }

    protected override async Task CleanupAsync(INetworkStorageStore store)
    {
        await ((SqliteNetworkStorageStore)store).DisposeAsync();
        TestDatabases.DeleteSqlite(_path);
    }
}

public sealed class PostgresStoreConformanceTests : StoreConformanceTests
{
    private readonly string _schema = TestDatabases.NewPostgresSchema();

    protected override async Task<INetworkStorageStore> CreateStoreAsync(ManualTimeProvider time)
    {
        Skip.If(TestDatabases.PostgresConnectionString is null, TestDatabases.PostgresSkipReason);
        var store = TestDatabases.CreatePostgres(_schema, time);
        await store.MigrateAsync(Ct);
        return store;
    }

    protected override async Task CleanupAsync(INetworkStorageStore store)
    {
        await ((PostgresNetworkStorageStore)store).DisposeAsync();
        await TestDatabases.DropPostgresSchemaAsync(_schema);
    }
}
