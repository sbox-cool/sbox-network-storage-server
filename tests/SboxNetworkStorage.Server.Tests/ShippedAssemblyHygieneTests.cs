using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ShippedAssemblyHygieneTests
{
    private static readonly string[] AssemblyNames =
    [
        "SboxNetworkStorage.Server", "SboxNetworkStorage.Application", "SboxNetworkStorage.Infrastructure",
        "SboxNetworkStorage.Contracts", "SboxNetworkStorage.Domain", "SboxNetworkStorage.Storage",
        "SboxNetworkStorage.Storage.Relational", "SboxNetworkStorage.Storage.Sqlite", "SboxNetworkStorage.Storage.Postgres"
    ];

    // These exact strings are wire-compatibility identifiers: excluded historical client route templates
    // (keeping their catalog classification avoids changing wire compatibility) and the .NET host's
    // single-file extraction variable, whose name contains "BUN". Descriptions/logs are not exempt.
    private static readonly HashSet<string> WireCompatibilityStrings = new(StringComparer.Ordinal)
    {
        "/v3/prototype/spacetimedb/:projectId/checkpoint",
        "/v3/prototype/spacetimedb/:projectId/collections",
        "/v3/prototype/spacetimedb/:projectId/endpoints",
        "/v3/prototype/spacetimedb/:projectId/game-values",
        "/v3/prototype/spacetimedb/:projectId/project",
        "/v3/prototype/spacetimedb/:projectId/queries",
        "/v3/prototype/spacetimedb/:projectId/rate-limit-rules",
        "/v3/prototype/spacetimedb/:projectId/status",
        "/v3/prototype/spacetimedb/:projectId/storage/:collectionId/:key",
        "/v3/prototype/spacetimedb/:projectId/workflows",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR="
    };

    private static readonly string[] RemovedTypes =
    [
        "ProxyErrorReporter", "ProxyResponseForwarder", "SboxcoolBackendOptions", "BackendOptions",
        "RequestStats", "IRequestStats", "DependencyTimingContext", "NetworkStorageManagementService",
        "NetworkStorageWriteSafety", "ISystemClock", "IWebsiteStorageClient", "IApplicationReadinessState",
        "DiagnosticsResponses", "NetworkStorageModeResolver", "INetworkStorageModeResolver",
        "NetworkStorageRuntimeMode", "NetworkStorageDispatchDecision", "NetworkStorageDispatchTarget",
        "InMemoryNetworkStorageStore"
    ];

    [Fact]
    public void ShippedAssembliesContainNoLegacyInfrastructureStringsOrTypes()
    {
        var violations = new List<string>();
        foreach (var assemblyName in AssemblyNames)
        {
            var fileName = assemblyName == "SboxNetworkStorage.Server" ? "sbox-ns.dll" : assemblyName + ".dll";
            using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, fileName));
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var name = metadata.GetString(metadata.GetTypeDefinition(handle).Name);
                if (ContainsAny(name, ["Scylla", "Bunny", "Shadow", "Candidate"])
                    || RemovedTypes.Contains(name, StringComparer.Ordinal)
                    || name.StartsWith("RouteOwnership", StringComparison.Ordinal)
                    || name.Contains("ProjectBackupService", StringComparison.Ordinal))
                    violations.Add($"{assemblyName}: type {name}");
            }

            // #US starts with a reserved zero byte. Each entry has a compressed byte length,
            // UTF-16 text and one terminal flag byte; padding has a zero length.
            var offset = 1;
            var heapSize = metadata.GetHeapSize(HeapIndex.UserString);
            var heap = pe.GetMetadata().GetReader(metadata.GetHeapMetadataOffset(HeapIndex.UserString), heapSize);
            heap.Offset = offset;
            while (offset < heapSize)
            {
                var length = heap.ReadCompressedInteger();
                if (length == 0)
                {
                    offset = heap.Offset;
                    continue;
                }
                var text = metadata.GetUserString(MetadataTokens.UserStringHandle(offset));
                if (ContainsAny(text, ["Bun", "SpacetimeDB", "Scylla", "Bunny"])
                    && !WireCompatibilityStrings.Contains(text))
                    violations.Add($"{assemblyName}: string {text}");
                heap.Offset += length;
                offset = heap.Offset;
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static bool ContainsAny(string value, string[] names)
        => names.Any(name => value.Contains(name, StringComparison.OrdinalIgnoreCase));
}
