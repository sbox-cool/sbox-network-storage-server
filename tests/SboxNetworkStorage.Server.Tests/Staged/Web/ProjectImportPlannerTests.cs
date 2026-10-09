using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Import;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>
/// Unit tests for the pure project-import core: conflict classification
/// (including records by key — the bug the old preview never detected),
/// per-resource resolution, id remapping, structural query-ref rewrite, and
/// up-front file validation. No the store needed.
/// </summary>
public sealed class ProjectImportPlannerTests
{
    // ── Preview: collision detection ──────────────────────────────────

    [Fact]
    public void Preview_DetectsRecordConflictsByKey()
    {
        using var doc = ProjectExportDocument.Parse($$"""
            {
              "export_version": 1, "project_id": "src", "exported_at": "t",
              "tables": {
                "records": { "col1": [
                  {{Record("p1")}},
                  {{Record("p3")}}
                ] }
              }
            }
            """);
        var existing = new ExistingProjectSnapshot
        {
            RecordKeysByCollection = Keys("col1", "p1", "p2"),
        };

        var report = ProjectImportPlanner.Preview(doc, existing);

        var records = report.Tables["records"];
        Assert.Equal(2, records.IncomingCount);
        Assert.Equal(1, records.ConflictCount); // p1 already exists → would overwrite
        Assert.Equal(1, records.NewCount);      // p3 is new
        Assert.Equal(2, records.ExistingCount); // p1, p2 already in target
    }

    [Fact]
    public void Preview_RecordsWithoutExistingCollection_AreAllNew()
    {
        using var doc = ProjectExportDocument.Parse($$"""
            { "export_version": 1, "project_id": "src",
              "tables": { "records": { "fresh": [ {{Record("a")}}, {{Record("b")}} ] } } }
            """);

        var report = ProjectImportPlanner.Preview(doc, ExistingProjectSnapshot.Empty);

        var records = report.Tables["records"];
        Assert.Equal(2, records.IncomingCount);
        Assert.Equal(0, records.ConflictCount);
        Assert.Equal(2, records.NewCount);
    }

    [Fact]
    public void Preview_DetectsResourceConflictsById_WithDetail()
    {
        using var doc = ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "src",
              "tables": { "collections": [
                { "collection_id": "colA", "name": "Players", "updated_at_unix_ms": "200" },
                { "collection_id": "colB", "name": "Items", "updated_at_unix_ms": "201" }
              ] } }
            """);
        var existing = new ExistingProjectSnapshot { Collections = Res(("colA", 100)) };

        var report = ProjectImportPlanner.Preview(doc, existing);

        var collections = report.Tables["collections"];
        Assert.Equal(2, collections.IncomingCount);
        Assert.Equal(1, collections.ConflictCount);
        Assert.Equal(1, collections.NewCount);
        var conflict = Assert.Single(collections.Conflicts);
        Assert.Equal("colA", conflict.Id);
        Assert.Equal("Players", conflict.Name);
        Assert.Equal("100", conflict.ExistingUpdatedAt);
        Assert.Equal("200", conflict.IncomingUpdatedAt);
    }

    [Fact]
    public void Preview_ScalarTables_ReportConflictOnlyWhenTheyExist()
    {
        using var doc = ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "src",
              "tables": {
                "game_values": [ { "payload_json": "{}", "version": "1" } ],
                "rate_limit_rules": [ { "rules_json": "[]", "version": "1" } ]
              } }
            """);
        // game_values already exists in the target; rate_limit_rules does not.
        var existing = new ExistingProjectSnapshot
        {
            ExistingScalarTables = new HashSet<string> { "game_values" },
        };

        var report = ProjectImportPlanner.Preview(doc, existing);

        var gameValues = report.Tables["game_values"];
        Assert.Equal(1, gameValues.IncomingCount);
        Assert.Equal(1, gameValues.ConflictCount); // exists -> will be replaced, not "new"
        Assert.Equal(0, gameValues.NewCount);

        var rateLimits = report.Tables["rate_limit_rules"];
        Assert.Equal(1, rateLimits.IncomingCount);
        Assert.Equal(0, rateLimits.ConflictCount); // absent -> genuinely new
        Assert.Equal(1, rateLimits.NewCount);
    }

    // ── Plan: resolution semantics ────────────────────────────────────

    [Fact]
    public void Plan_Skip_LeavesConflictingResourceUnwritten()
    {
        using var doc = TwoCollections();
        var existing = new ExistingProjectSnapshot { Collections = Res(("colA", 1)) };
        var plan = WithCollections(new ImportResolutionPlan(ImportResolution.Skip));

        var result = ProjectImportPlanner.Plan(doc, existing, plan, Counter());

        Assert.DoesNotContain(result.Actions, a => a.Table == "collections" && a.WriteId == "colA");
        Assert.Contains(result.Actions, a => a.Table == "collections" && a.WriteId == "colB"); // new id still imported
    }

    [Fact]
    public void Plan_Overwrite_WritesConflictUnderSameId()
    {
        using var doc = TwoCollections();
        var existing = new ExistingProjectSnapshot { Collections = Res(("colA", 1)) };
        var plan = WithCollections(new ImportResolutionPlan(ImportResolution.Overwrite));

        var result = ProjectImportPlanner.Plan(doc, existing, plan, Counter());

        Assert.Contains(result.Actions, a => a.Table == "collections" && a.WriteId == "colA");
        Assert.False(result.Remappings.ContainsKey("collections"));
    }

    [Fact]
    public void Plan_PerResourceOverride_BeatsTableDefault()
    {
        using var doc = ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "src",
              "tables": { "collections": [
                { "collection_id": "colA", "name": "A" },
                { "collection_id": "colC", "name": "C" }
              ] } }
            """);
        var existing = new ExistingProjectSnapshot { Collections = Res(("colA", 1), ("colC", 1)) };
        // Default skip, but override colA → overwrite.
        var plan = WithCollections(new ImportResolutionPlan(
            ImportResolution.Skip,
            new Dictionary<string, ImportResolution> { ["colA"] = ImportResolution.Overwrite }));

        var result = ProjectImportPlanner.Plan(doc, existing, plan, Counter());

        Assert.Contains(result.Actions, a => a.Table == "collections" && a.WriteId == "colA"); // overridden
        Assert.DoesNotContain(result.Actions, a => a.Table == "collections" && a.WriteId == "colC"); // skipped
    }

    [Fact]
    public void Plan_Copy_RemapsCollectionAndItsRecords()
    {
        using var doc = ProjectExportDocument.Parse($$"""
            { "export_version": 1, "project_id": "src",
              "tables": {
                "collections": [ { "collection_id": "colA", "name": "A" } ],
                "records": { "colA": [ {{Record("k1")}} ] }
              } }
            """);
        var existing = new ExistingProjectSnapshot { Collections = Res(("colA", 1)) };
        var plan = WithCollections(new ImportResolutionPlan(ImportResolution.Copy));

        var result = ProjectImportPlanner.Plan(doc, existing, plan, Counter());

        Assert.Equal("new1", Assert.Single(result.Remappings["collections"]).Value);
        var collection = Assert.Single(result.Actions, a => a.Table == "collections");
        Assert.Equal("new1", collection.WriteId);
        var record = Assert.Single(result.Actions, a => a.Table == "records");
        Assert.Equal("new1", record.CollectionId); // record follows the copied collection
        Assert.Equal("k1", record.WriteId);
    }

    [Fact]
    public void Plan_NewProject_RemapsEverythingAndRewritesQueryRefs()
    {
        using var doc = ProjectExportDocument.Parse($$"""
            { "export_version": 1, "project_id": "src",
              "tables": {
                "collections": [ { "collection_id": "col1", "name": "A" } ],
                "queries": [ { "query_id": "q1", "name": "Q", "definition_json": "{\"sources\":[{\"collectionId\":\"col1\"}]}" } ],
                "records": { "col1": [ {{Record("k1")}} ] }
              } }
            """);
        var plan = ImportApplyPlan.OverwriteAll(createAsNewProject: true, newProjectName: "New");

        var result = ProjectImportPlanner.Plan(doc, ExistingProjectSnapshot.Empty, plan, Counter());

        var collection = Assert.Single(result.Actions, a => a.Table == "collections");
        Assert.Equal("new1", collection.WriteId); // fresh id

        var query = Assert.Single(result.Actions, a => a.Table == "queries");
        Assert.NotNull(query.DefinitionOverride);
        Assert.Contains("new1", query.DefinitionOverride); // ref rewritten to remapped collection
        Assert.DoesNotContain("col1", query.DefinitionOverride);

        var record = Assert.Single(result.Actions, a => a.Table == "records");
        Assert.Equal("new1", record.CollectionId);
    }

    [Fact]
    public void Plan_RecordsAreAlwaysWritten()
    {
        using var doc = ProjectExportDocument.Parse($$"""
            { "export_version": 1, "project_id": "src",
              "tables": { "records": { "col1": [ {{Record("a")}}, {{Record("b")}} ] } } }
            """);
        // Even with a Skip default, records carry no resolution — they always import.
        var plan = WithCollections(new ImportResolutionPlan(ImportResolution.Skip));

        var result = ProjectImportPlanner.Plan(doc, ExistingProjectSnapshot.Empty, plan, Counter());

        Assert.Equal(2, result.Actions.Count(a => a.Table == "records"));
    }

    [Fact]
    public void Plan_NeverWritesProjectsRow()
    {
        using var doc = ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "src",
              "tables": { "projects": [ { "workspace_id": "ws", "storage_owner_user_id": "9" } ] } }
            """);

        var result = ProjectImportPlanner.Plan(doc, ExistingProjectSnapshot.Empty,
            ImportApplyPlan.OverwriteAll(), Counter());

        Assert.DoesNotContain(result.Actions, a => a.Table == "projects");
    }

    // ── Structural query-ref remap ────────────────────────────────────

    [Fact]
    public void RemapQueryCollectionRefs_OnlyRewritesCollectionIdFields()
    {
        var remap = new Dictionary<string, string> { ["old"] = "fresh" };
        // "old" appears both as a collectionId (should change) and a name (must not).
        var result = ProjectImportPlanner.RemapQueryCollectionRefs(
            "{\"sources\":[{\"collectionId\":\"old\"}],\"name\":\"old\"}", remap);

        Assert.Contains("\"collectionId\":\"fresh\"", result);
        Assert.Contains("\"name\":\"old\"", result); // unrelated field untouched
    }

    [Fact]
    public void RemapQueryCollectionRefs_InvalidJson_ReturnsInput()
    {
        var remap = new Dictionary<string, string> { ["old"] = "fresh" };
        Assert.Equal("not json", ProjectImportPlanner.RemapQueryCollectionRefs("not json", remap));
    }

    // ── File validation ───────────────────────────────────────────────

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ \"project_id\": \"p\", \"export_version\": 1 }")]          // missing tables
    [InlineData("{ \"export_version\": 1, \"tables\": {} }")]                  // missing project_id
    [InlineData("{ \"project_id\": \"p\", \"tables\": {} }")]                  // missing export_version
    [InlineData("[]")]                                                          // not an object
    public void Parse_RejectsMalformedExport(string json)
    {
        Assert.Throws<InvalidProjectExportException>(() => ProjectExportDocument.Parse(json));
    }

    [Fact]
    public void Parse_AcceptsValidExport()
    {
        using var doc = ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "proj_x", "exported_at": "2026-01-01", "tables": {} }
            """);
        Assert.Equal("proj_x", doc.ProjectId);
        Assert.Equal(1, doc.ExportVersion);
        Assert.Equal("2026-01-01", doc.ExportedAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static string Record(string key)
        => $$"""{ "record_key": "{{key}}", "record_id": "{{key}}", "payload_json": "{}", "deleted": "False", "version": "1", "updated_at_unix_ms": "1", "created_at_unix_ms": "1" }""";

    private static ProjectExportDocument TwoCollections()
        => ProjectExportDocument.Parse("""
            { "export_version": 1, "project_id": "src",
              "tables": { "collections": [
                { "collection_id": "colA", "name": "A" },
                { "collection_id": "colB", "name": "B" }
              ] } }
            """);

    private static ImportApplyPlan WithCollections(ImportResolutionPlan collections)
        => new(false, null, false, collections,
            new ImportResolutionPlan(), new ImportResolutionPlan(),
            new ImportResolutionPlan(), new ImportResolutionPlan());

    private static ExistingResourceTable Res(params (string Id, long Ts)[] ids)
        => new(ids.ToDictionary(x => x.Id, x => (long?)x.Ts));

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Keys(string collection, params string[] keys)
        => new Dictionary<string, IReadOnlySet<string>> { [collection] = new HashSet<string>(keys) };

    /// <summary>Deterministic id generator: new1, new2, ... for stable assertions.</summary>
    private static Func<string> Counter()
    {
        var n = 0;
        return () => $"new{++n}";
    }
}
