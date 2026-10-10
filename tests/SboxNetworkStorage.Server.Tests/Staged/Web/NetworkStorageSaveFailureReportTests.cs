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
using SboxNetworkStorage.Server.Hosting;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Covers the client save-failure report endpoint (<c>POST /api/network-storage/{projectId}/save-failure</c>).
/// A game posts here when a save returned 200 but a read-back could not confirm it persisted — a
/// silent drop the server never sees on the proxied write path. The route lives under
/// <c>/api/network-storage/*</c> (not <c>/api/storage/*</c>) so the live nginx reaches the .NET
/// website today instead of the legacy server data plane. The endpoint must authenticate, fire a
/// Discord alert, and record a diagnostic analytics event for cause correlation.
/// </summary>
public abstract class NetworkStorageSaveFailureReportTests<TFactory> : IClassFixture<TFactory>
    where TFactory : SelfHostFactory
{
    private readonly SelfHostFactory _factory;

    protected NetworkStorageSaveFailureReportTests(TFactory factory)
    {
        Skip.IfNot(factory.IsAvailable, factory.SkipReason);
        _factory = factory;
    }

    private async Task<(HttpClient client, RecordingAlertSink sink, RecordingAnalytics analytics, SelfHostProject project)> CreateAsync(bool projectEnabled = true)
    {
        var sink = new RecordingAlertSink();
        var analytics = new RecordingAnalytics();
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<INetworkStorageErrorAlertSink>();
                services.AddSingleton<INetworkStorageErrorAlertSink>(sink);
                services.RemoveAll<IPlayerAnalyticsService>();
                services.AddSingleton<IPlayerAnalyticsService>(analytics);
            });
        });
        var project = await factory.CreateProjectAsync("Save failure");
        if (!projectEnabled)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<INetworkStorageProjectService>().UpdateProjectSettingsAsync(
                NetworkStorageServices.LocalOwnerUserId, project.ProjectId, "project", new() { ["enabled"] = "false" }, CancellationToken.None);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        return (client, sink, analytics, project);
    }

    private static string Url(SelfHostProject project, bool withKey = true)
        => $"/api/network-storage/{project.ProjectId}/save-failure" + (withKey ? $"?apiKey={project.PublicKey}" : "");

    [SkippableFact]
    public async Task SaveFailure_WithValidKey_FiresDiscordAlertAndRecordsDiagnosticEvent()
    {
        var (client, sink, analytics, project) = await CreateAsync();
        using var _ = client;

        using var response = await client.PostAsJsonAsync(Url(project),
            new { collectionId = "players", recordKey = "76561198000000000", reason = "data mismatch", expectedSeq = 7, observedSeq = 5, attempts = 3 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var alert = Assert.Single(sink.Errors);
        Assert.Equal("SAVE_NOT_CONFIRMED", alert.Code);
        Assert.Equal("save.unconfirmed", alert.Operation);
        Assert.Equal(project.ProjectId, alert.ProjectId);
        Assert.Equal("players", alert.CollectionId);
        Assert.Equal("76561198000000000", alert.RecordKey);
        Assert.Contains("expectedSeq=7", alert.Message);
        Assert.Contains("observedSeq=5", alert.Message);

        var evt = Assert.Single(analytics.Events);
        Assert.Equal("record.save_unconfirmed", evt.EventType);
        Assert.Equal(project.ProjectId, evt.ProjectId);
        Assert.Equal("players", evt.CollectionId);
        Assert.Equal("76561198000000000", evt.RecordKey);
    }

    [SkippableFact]
    public async Task SaveFailure_MissingKey_Returns401_AndDoesNotAlert()
    {
        var (client, sink, analytics, project) = await CreateAsync();
        using var _ = client;

        using var response = await client.PostAsJsonAsync(Url(project, withKey: false), new { collectionId = "players" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(sink.Errors);
        Assert.Empty(analytics.Events);
    }

    [SkippableFact]
    public async Task SaveFailure_NonObjectBody_Returns400_AndDoesNotAlert()
    {
        var (client, sink, _, project) = await CreateAsync();
        using var __ = client;

        using var response = await client.PostAsJsonAsync(Url(project), 42);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sink.Errors);
    }

    [SkippableFact]
    public async Task SaveFailure_DisabledProject_Returns403_AndDoesNotAlert()
    {
        var (client, sink, analytics, project) = await CreateAsync(projectEnabled: false);
        using var _ = client;

        using var response = await client.PostAsJsonAsync(Url(project), new { collectionId = "players", recordKey = "76561198000000000" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(sink.Errors);
        Assert.Empty(analytics.Events);
    }

    [SkippableTheory]
    [InlineData("[click me](https://evil.example)", "76561198000000000")]
    [InlineData("players", "a\r\nBcc: victim@example.com")]
    public async Task SaveFailure_MalformedIds_Return400_AndDoNotAlert(string collectionId, string recordKey)
    {
        var (client, sink, _, project) = await CreateAsync();
        using var __ = client;

        using var response = await client.PostAsJsonAsync(Url(project), new { collectionId, recordKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(sink.Errors);
    }

    [SkippableFact]
    public async Task SaveFailure_LongReasonIsCutInTheAlert()
    {
        var (client, sink, _, project) = await CreateAsync();
        using var __ = client;

        using var response = await client.PostAsJsonAsync(Url(project),
            new { collectionId = "players", recordKey = "76561198000000000", reason = new string('x', 5_000) });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var alert = Assert.Single(sink.Errors);
        Assert.Contains(new string('x', 200) + "…", alert.Message);
        Assert.DoesNotContain(new string('x', 201), alert.Message);
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
}

public sealed class NetworkStorageSaveFailureReportTests_Sqlite(SqliteHostFactory factory) : NetworkStorageSaveFailureReportTests<SqliteHostFactory>(factory);

public sealed class NetworkStorageSaveFailureReportTests_Postgres(PostgresHostFactory factory) : NetworkStorageSaveFailureReportTests<PostgresHostFactory>(factory);
