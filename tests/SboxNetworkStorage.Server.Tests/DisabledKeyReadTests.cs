using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Server.Hosting;
using SboxNetworkStorage.Server.Tests.Hosting;

namespace SboxNetworkStorage.Server.Tests;

public abstract class DisabledKeyReadTests<TFactory> : IClassFixture<TFactory> where TFactory : SelfHostFactory
{
    private readonly TFactory _factory;
    protected DisabledKeyReadTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    [SkippableTheory]
    [InlineData(false, "/v3/queries/{0}/missing")]
    [InlineData(true, "/v3/queries/{0}/missing")]
    [InlineData(false, "/api/storage/{0}/queries/missing")]
    [InlineData(true, "/v3/storage/{0}/scores/76561198000000123/ledger")]
    [InlineData(false, "/v3/storage/{0}/scores/76561198000000123/ledger")]
    public async Task DisabledKeysAreRejectedBeforeAnyResourceLookup(bool secret, string route)
    {
        var project = await _factory.CreateProjectAsync();
        var key = secret ? project.SecretKey : project.PublicKey;
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>()
            .ToggleProjectKeyAsync(NetworkStorageServices.LocalOwnerUserId, project.ProjectId,
                secret ? StorageKeyCrypto.MaskSecretKey(key) : key, CancellationToken.None);
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, string.Format(route, project.ProjectId));
        request.Headers.Add("x-api-key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}

public sealed class SqliteDisabledKeyReadTests(SqliteHostFactory factory) : DisabledKeyReadTests<SqliteHostFactory>(factory);
public sealed class PostgresDisabledKeyReadTests(PostgresHostFactory factory) : DisabledKeyReadTests<PostgresHostFactory>(factory);
