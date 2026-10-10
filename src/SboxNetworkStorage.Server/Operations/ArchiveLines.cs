using System.Runtime.CompilerServices;
using System.Text;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Reads the JSON-lines entries of an archive with a per-line size cap, so one huge line
/// cannot become a string of hundreds of megabytes before it is even parsed.
/// </summary>
internal static class ArchiveLines
{
    /// <summary>Largest accepted line in characters. Exported rows are far smaller: workspace objects are bounded by the management request limit.</summary>
    public const int MaxLineChars = 32 * 1024 * 1024;

    /// <summary>Yields every line, empty ones included, so callers can keep their own line numbers.</summary>
    public static async IAsyncEnumerable<string> ReadAsync(Stream content, string entryName, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(content, new UTF8Encoding(false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
        var buffer = new char[64 * 1024];
        var line = new StringBuilder();
        long lineNumber = 1;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var start = 0;
            int newline;
            while ((newline = Array.IndexOf(buffer, '\n', start, read - start)) >= 0)
            {
                Append(line, buffer, start, newline - start, entryName, lineNumber);
                yield return Take(line, entryName, lineNumber);
                lineNumber++;
                start = newline + 1;
            }

            Append(line, buffer, start, read - start, entryName, lineNumber);
        }

        if (line.Length > 0)
        {
            yield return Take(line, entryName, lineNumber);
        }
    }

    // One character over the cap is buffered so a trailing '\r' of a CRLF line does not count.
    private static void Append(StringBuilder line, char[] buffer, int start, int count, string entryName, long lineNumber)
    {
        if (line.Length + count > MaxLineChars + 1)
        {
            throw TooLong(entryName, lineNumber);
        }

        line.Append(buffer, start, count);
    }

    private static string Take(StringBuilder line, string entryName, long lineNumber)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        if (line.Length > MaxLineChars)
        {
            throw TooLong(entryName, lineNumber);
        }

        var text = line.ToString();
        line.Clear();
        return text;
    }

    private static ExportArchiveException TooLong(string entryName, long lineNumber)
        => new($"{entryName}:{lineNumber}: the line is longer than the {MaxLineChars / (1024 * 1024)} MiB limit.");
}
