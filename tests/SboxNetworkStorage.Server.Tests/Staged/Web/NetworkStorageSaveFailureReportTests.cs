using SboxNetworkStorage.Server.Tests.Hosting;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SboxNetworkStorage.Domain.Workspace;
using SboxNetworkStorage.Application.NetworkStorage;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Covers the client save-failure report endpoint (<c>POST /api/network-storage/{projectId}/save-failure</c>).
/// A game posts here when a save returned 200 but a read-back could not confirm it persisted — a
/// silent drop the server never sees on the proxied write path. The route lives under
/// <c>/api/network-storage/*</c> (not <c>/api/storage/*</c>) so the live nginx reaches the .NET
/// website today instead of the legacy Bun data plane. The endpoint must authenticate, fire a
/// Discord alert, and record a diagnostic analytics event for cause correlation.
/// </summary>
public abstract class NetworkStorageSaveFailureReportTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private const string ApiKey = "sk-test-savefail";
    private const string ProjectId = "demo-project";

    private readonly SelfHostFactory _factory;

    protected NetworkStorageSaveFailureReportTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private (HttpClient client, RecordingAlertSink sink, RecordingAnalytics analytics) Create()
    {
        var sink = new RecordingAlertSink();
        var analytics = new RecordingAnalytics();
        var client = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStorageApiKeyResolver>();
                services.AddScoped<IStorageApiKeyResolver>(_ => new FakeKeyResolver(ApiKey, ProjectId));
                services.RemoveAll<INetworkStorageErrorAlertSink>();
                services.AddSingleton<INetworkStorageErrorAlertSink>(sink);
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService>(analytics);
            });
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return (client, sink, analytics);
    }

    [SkippableFact]
    public async Task SaveFailure_WithValidKey_FiresDiscordAlertAndRecordsDiagnosticEvent()
    {
        var (client, sink, analytics) = Create();
        using var _ = client;

        using var response = await client.PostAsJsonAsync(
            $"/api/network-storage/{ProjectId}/save-failure?apiKey={ApiKey}",
            new { collectionId = "players", recordKey = "76561198000000000", reason = "data mismatch", expectedSeq = 7, observedSeq = 5, attempts = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = Assert.Single(sink.Errors);
        Assert.Equal("SAVE_NOT_CONFIRMED", alert.Code);
        Assert.Equal("save.unconfirmed", alert.Operation);
        Assert.Equal(ProjectId, alert.ProjectId);
        Assert.Equal("players", alert.CollectionId);
        Assert.Equal("76561198000000000", alert.RecordKey);
        Assert.Contains("expectedSeq=7", alert.Message);
        Assert.Contains("observedSeq=5", alert.Message);

        var evt = Assert.Single(analytics.Events);
        Assert.Equal("record.save_unconfirmed", evt.EventType);
        Assert.Equal(ProjectId, evt.ProjectId);
        Assert.Equal("players", evt.CollectionId);
        Assert.Equal("76561198000000000", evt.RecordKey);
    }

    [SkippableFact]
    public async Task SaveFailure_MissingKey_Returns401_AndDoesNotAlert()
    {
        var (client, sink, analytics) = Create();
        using var _ = client;

        using var response = await client.PostAsJsonAsync(
            $"/api/network-storage/{ProjectId}/save-failure",
            new { collectionId = "players" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(sink.Errors);
        Assert.Empty(analytics.Events);
    }

    [SkippableFact]
    public async Task SaveFailure_NonObjectBody_Returns400_AndDoesNotAlert()
    {
        var (client, sink, _) = Create();
        using var __ = client;

        using var response = await client.PostAsJsonAsync(
            $"/api/network-storage/{ProjectId}/save-failure?apiKey={ApiKey}", 42);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sink.Errors);
    }

    private sealed class RecordingAlertSink : INetworkStorageErrorAlertSink
    {
        public List<NetworkStorageError> Errors { get; } = new();

        public Task NotifyAsync(NetworkStorageError error, CancellationToken cancellationToken)
        {
            lock (Errors) Errors.Add(error);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAnalytics : IPlayerAnalyticsService
    {
        public List<PlayerEventRequest> Events { get; } = new();

        public Task RecordEventAsync(PlayerEventRequest request, CancellationToken cancellationToken)
        {
            lock (Events) Events.Add(request);
            return Task.CompletedTask;
        }

        public Task RecordEndpointEventAsync(string projectId, string steamId, string endpointSlug, string eventType, IReadOnlyDictionary<string, object>? payload, IReadOnlyList<TrackedFieldDelta>? trackedFieldDeltas, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeKeyResolver(string validKey, string projectId) : IStorageApiKeyResolver
    {
        public Task<StorageApiKeyAuthResult?> ResolveApiKeyAsync(string apiKey, string project, CancellationToken cancellationToken)
            => Task.FromResult(
                string.Equals(apiKey, validKey, StringComparison.Ordinal) && string.Equals(project, projectId, StringComparison.Ordinal)
                    ? new StorageApiKeyAuthResult(42, project, true, "secret")
                    : null);
    }
}

public sealed class NetworkStorageSaveFailureReportTests_Sqlite(SqliteHostFactory factory) : NetworkStorageSaveFailureReportTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageSaveFailureReportTests_Postgres(PostgresHostFactory factory) : NetworkStorageSaveFailureReportTests<PostgresHostFactory>(factory);
