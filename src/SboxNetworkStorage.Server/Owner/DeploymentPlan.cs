namespace SboxNetworkStorage.Server.Owner;

/// <summary>Severity labels for <see cref="PlanIssue"/>. Blockers stop approval; warnings are shown but do not.</summary>
public static class PlanSeverity
{
    public const string Blocker = "blocker";
    public const string Warning = "warning";
}

/// <summary>
/// One definition in a plan. <paramref name="Action"/> is create, update, unchanged, delete, invalid or
/// module-retained. <paramref name="Publish"/> is live, staged, or live-fallback (next target without staged data).
/// </summary>
public sealed record PlanItem(string Action, string Kind, string Id, string Publish, IReadOnlyList<PlanReference> References);

/// <summary>A static reference. Status is resolved, missing, or unknown (dynamic or built at runtime).</summary>
public sealed record PlanReference(string Kind, string Id, string Path, string Status);

/// <summary>A finding about the plan.</summary>
public sealed record PlanIssue(string Severity, string Kind, string Id, string Message);

/// <summary>A static dependent of a changed definition, or a possible dependent whose reference is dynamic.</summary>
public sealed record PlanDependent(string Kind, string Id, string Target, string ResourceKind, string ResourceId, string Status);

/// <summary>
/// A previewed plan. <paramref name="Hash"/> binds approval to the exact proposal and the project state it was
/// previewed against; any change to either produces a different hash.
/// </summary>
public sealed record DeploymentPlan(string Target, string Hash, IReadOnlyList<PlanItem> Items,
    IReadOnlyList<PlanIssue> Issues, bool HasRevisionData, IReadOnlyList<PlanDependent> Dependents,
    System.Text.Json.JsonElement RevisionPolicy)
{
    public bool Blocked => Issues.Any(issue => issue.Severity == PlanSeverity.Blocker);
}

/// <summary>Model for the deployment console page.</summary>
public sealed record DeploymentPageModel(string ProjectId, string ProjectName, string Proposal, DeploymentPlan? Plan, string? Error = null);
