namespace SboxNetworkStorage.Application.NetworkStorage.Media;

/// <summary>
/// Identifies uploads by their magic bytes and reads pixel dimensions from the header.
/// Every read is bounds-checked; malformed input yields "unrecognized" or zero dimensions,
/// never an exception.
/// </summary>
public static class MediaSniffer
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Webp = "image/webp";

    public static readonly IReadOnlyList<string> SupportedContentTypes = [Png, Jpeg, Webp];

    /// <summary>Returns null when the bytes match no supported type. Width/height are 0 when the header is unreadable.</summary>
    public static SniffResult? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(PngSignature))
            return new SniffResult(Png, PngSize(data));
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return new SniffResult(Jpeg, JpegSize(data));
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return new SniffResult(Webp, WebpSize(data));
        return null;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static (int Width, int Height) PngSize(ReadOnlySpan<byte> d)
    {
        // Signature (8) + IHDR chunk: length(4) "IHDR"(4) width(4 BE) height(4 BE).
        if (d.Length < 24 || !d[12..16].SequenceEqual("IHDR"u8)) return (0, 0);
        return (ReadBe32(d, 16), ReadBe32(d, 20));
    }

    private static (int Width, int Height) JpegSize(ReadOnlySpan<byte> d)
    {
        var i = 2;
        while (i + 3 < d.Length)
        {
            if (d[i] != 0xFF) return (0, 0);
            // Skip fill bytes before a marker.
            while (i < d.Length && d[i] == 0xFF) i++;
            if (i >= d.Length) return (0, 0);
            var marker = d[i];
            i++;
            // Standalone markers carry no length: SOI, EOI, TEM, RSTn.
            if (marker is 0xD8 or 0xD9 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            // Start of scan: the image data follows, so the frame header has already passed.
            if (marker == 0xDA || i + 1 >= d.Length) return (0, 0);
            var length = (d[i] << 8) | d[i + 1];
            if (length < 2) return (0, 0);
            // SOF0..SOF15 except DHT (C4), JPG (C8) and DAC (CC) are frame headers.
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // length(2) precision(1) height(2) width(2) ...
                if (i + 7 > d.Length) return (0, 0);
                var height = (d[i + 3] << 8) | d[i + 4];
                var width = (d[i + 5] << 8) | d[i + 6];
                return (width, height);
            }
            i += length;
        }
        return (0, 0);
    }

    private static (int Width, int Height) WebpSize(ReadOnlySpan<byte> d)
    {
        // Chunk fourcc sits at 12..16, the bitstream payload starts at 20.
        if (d.Length < 30) return (0, 0);
        if (d[12..16].SequenceEqual("VP8X"u8))
        {
            // Canvas width-1 and height-1 are 24-bit little endian at 24 and 27.
            return (ReadLe24(d, 24) + 1, ReadLe24(d, 27) + 1);
        }
        if (d[12..16].SequenceEqual("VP8L"u8))
        {
            if (d[20] != 0x2F) return (0, 0);
            var v = ReadLe32(d, 21);
            return ((int)(v & 0x3FFF) + 1, (int)((v >> 14) & 0x3FFF) + 1);
        }
        if (d[12..16].SequenceEqual("VP8 "u8))
        {
            // Lossy keyframe: 3-byte frame tag, then start code 9D 01 2A at 23..25, then 14-bit dims.
            if (d[23] != 0x9D || d[24] != 0x01 || d[25] != 0x2A) return (0, 0);
            return (ReadLe16(d, 26) & 0x3FFF, ReadLe16(d, 28) & 0x3FFF);
        }
        return (0, 0);
    }

    private static int ReadBe32(ReadOnlySpan<byte> d, int at)
        => (d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3];

    private static int ReadLe16(ReadOnlySpan<byte> d, int at)
        => d[at] | (d[at + 1] << 8);

    private static int ReadLe24(ReadOnlySpan<byte> d, int at)
        => d[at] | (d[at + 1] << 8) | (d[at + 2] << 16);

    private static uint ReadLe32(ReadOnlySpan<byte> d, int at)
        => (uint)(d[at] | (d[at + 1] << 8) | (d[at + 2] << 16) | (d[at + 3] << 24));
}

/// <summary>Detected content type and pixel size (0×0 when the header could not be read).</summary>
public readonly record struct SniffResult(string ContentType, (int Width, int Height) Size)
{
    public int Width => Size.Width;
    public int Height => Size.Height;
}
