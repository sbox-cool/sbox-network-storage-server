using System.Text;

namespace SboxNetworkStorage.EndpointSpike.Sandbox;

/// <summary>Raised when one protocol line exceeds its framing ceiling before a newline arrives.</summary>
public sealed class ProtocolLimitException(string message) : Exception(message);

/// <summary>
/// Reads newline-delimited UTF-8 lines with a hard per-line ceiling. Never buffers more than one line plus one
/// chunk, so a flooding worker cannot grow supervisor memory without bound.
/// </summary>
public sealed class BoundedLineReader(Stream stream, int maxLineBytes)
{
    private readonly byte[] _chunk = new byte[64 * 1024];
    private int _start;
    private int _end;

    /// <summary>Returns the next line without its terminator, or null at end of stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await stream.ReadAsync(_chunk.AsMemory(), ct);
                if (_end == 0)
                {
                    return line.Length == 0 ? null : Decode(line);
                }
            }

            var newline = Array.IndexOf(_chunk, (byte)'\n', _start, _end - _start);
            var segmentEnd = newline < 0 ? _end : newline;
            line.Write(_chunk, _start, segmentEnd - _start);
            if (line.Length > maxLineBytes)
            {
                throw new ProtocolLimitException($"Protocol line exceeded {maxLineBytes} bytes.");
            }

            if (newline < 0)
            {
                _start = _end;
                continue;
            }

            _start = newline + 1;
            return Decode(line);
        }
    }

    private static string Decode(MemoryStream line)
    {
        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length);
        return text.EndsWith('\r') ? text[..^1] : text;
    }
}
