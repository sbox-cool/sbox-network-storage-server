using System.Globalization;
using System.Text;
using System.Text.Json;
using SboxNetworkStorage.Application.NetworkStorage;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Infrastructure.NetworkStorage;
using SboxNetworkStorage.Infrastructure.NetworkStorage.Storage;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// YAML source handling shared by the dashboard definition editor, version history and the coding-agent
/// backend (<c>sbox-ns dev</c>): parse and wrap source text, check it with the save-time validator, save
/// it through the owner write path, and render stored definitions back to source.
/// </summary>
internal static class OwnerResourceSource
{
    public static readonly IReadOnlyList<string> Kinds = ["collection", "endpoint", "workflow", "query", "game-values"];

    public static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind);

    /// <summary>Source text when the definition was authored as YAML/JSON source, otherwise the definition rendered as YAML.</summary>
    public static string DisplayText(JsonElement definition)
        => definition.ValueKind == JsonValueKind.Object && definition.TryGetProperty("sourceText", out var source)
           && source.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(source.GetString())
            ? source.GetString()!
            : OwnerYamlDefinitions.ToYaml(definition);

    /// <summary>Where the editor Sync Tool keeps this definition under <c>Editor/Network Storage/</c>.</summary>
    public static string SourcePath(string kind, string id) => $"{kind}/{id}.yml";

    public static JsonElement Wrap(string id, string sourceText, string kind) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sourceText"] = sourceText,
            ["sourceFormat"] = "yaml",
            ["sourcePath"] = SourcePath(kind, id),
            ["authoringMode"] = "dashboard",
            ["sourceVersion"] = 1,
        });

    /// <summary>
    /// Parses YAML (or deprecated JSON) source into the resource the owner write path saves. <paramref name="id"/>
    /// is the existing id when editing; a source that names another id is refused.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, too large, or has no valid id.</exception>
    /// <exception cref="JsonException">The text is not valid YAML or JSON.</exception>
    public static JsonElement Parse(string kind, string? definition, string? id, int maxPayloadBytes, out string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(definition)) throw new ArgumentException("Enter a resource definition before saving.");
        if (Encoding.UTF8.GetByteCount(definition) > maxPayloadBytes) throw new ArgumentException($"Definition exceeds the store limit of {maxPayloadBytes:N0} bytes.");
        var parsed = OwnerYamlDefinitions.ParseDefinition(definition, out var wasJson);
        resourceId = null;
        // Game values are one document per project; the parsed object is stored as-is.
        if (kind == "game-values") return parsed;
        resourceId = OwnerProjectResources.Text(parsed, "id") ?? OwnerProjectResources.Text(parsed, kind == "endpoint" ? "slug" : "name");
        if (resourceId is null || !StorageIdValidation.IsValidCollectionId(resourceId))
            throw new ArgumentException("Provide an id containing only letters, numbers, underscores or hyphens (maximum 128 characters). Keep the same id when editing.");
        if (id is not null && id != resourceId) throw new ArgumentException("An existing resource's id cannot be changed. Create a new resource instead.");
        return wasJson ? parsed : Wrap(resourceId, definition, kind);
    }

    /// <summary>Parses the text and runs the save-time validator without saving. Parse failures become one INVALID_DEFINITION error.</summary>
    public static IReadOnlyList<DefinitionDiagnostic> Check(string kind, string? definition, string? id, OwnerProjectResources resources,
        int maxPayloadBytes, out JsonElement? source, out string? resourceId)
    {
        source = null;
        resourceId = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(definition)) source = OwnerYamlDefinitions.ParseDefinition(definition, out _);
            var resource = Parse(kind, definition, id, maxPayloadBytes, out resourceId);
            return NetworkStorageDefinitionValidator.ValidateResource(resource, kind, resources.ValidationContext(resourceId), out _);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return [new DefinitionDiagnostic("error", "INVALID_DEFINITION", error.Message, "/")];
        }
    }

    /// <summary>
    /// Validates, saves through <see cref="ManagementMutationHandler.SaveOwnerResourceAsync"/> and audits a successful
    /// save as <c>resource.save</c> by <paramref name="actorType"/>. The result is null when diagnostics block saving.
    /// </summary>
    public static async Task<(IReadOnlyList<DefinitionDiagnostic> Diagnostics, NetworkStorageResult? Result)> SaveAsync(
        ManagementMutationHandler mutations, IAuditLogger audit, string actorType, string projectId, string kind,
        JsonElement resource, string? resourceId, OwnerProjectResources resources, bool targetsNext, CancellationToken ct)
    {
        var diagnostics = NetworkStorageDefinitionValidator.ValidateResource(resource, kind, resources.ValidationContext(resourceId), out _);
        if (diagnostics.Any(item => item.IsError)) return (diagnostics, null);
        var result = await mutations.SaveOwnerResourceAsync(OwnerProjectScope.Owner, projectId, kind, resource, ct,
            versionSource: actorType, targetsNext: targetsNext);
        if (result.StatusCode < 400)
        {
            object summary = targetsNext ? new { kind, id = resourceId, target = "next" } : new { kind, id = resourceId };
            await audit.LogActionAsync(new AuditLogRequest(projectId, OwnerProjectScope.Owner.ToString(CultureInfo.InvariantCulture), "resource.save",
                new { type = actorType }, new { kind, id = resourceId }, summary, null, null), ct);
        }
        return (diagnostics, result);
    }
}
