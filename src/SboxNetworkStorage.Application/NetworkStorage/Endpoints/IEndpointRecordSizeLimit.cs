namespace SboxNetworkStorage.Application.NetworkStorage.Endpoints;

/// <summary>
/// Optional capability exposing the authoritative per-record payload limit of the
/// store behind an <see cref="IEndpointShadowDataSource"/>. The live-serve flush
/// in <see cref="NativeEndpointShadowExecutor"/> probes for this interface to
/// reject an over-limit record as 413 <c>PAYLOAD_TOO_LARGE</c> BEFORE any write
/// is applied (zero mutation). Data sources that do not expose a limit keep the
/// previous behavior: the store throws on flush and the caller fails closed 500.
/// </summary>
public interface IEndpointRecordSizeLimit
{
    /// <summary>Maximum accepted per-record JSON payload size, in UTF-8 bytes.</summary>
    int MaxPayloadBytes { get; }
}
