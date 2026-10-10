using System.Text;
using SboxNetworkStorage.Server.Operations;

namespace SboxNetworkStorage.Server.Tests;

public sealed class ArchiveLinesTests
{
    [Fact]
    public async Task LinesUpToTheCapAreReadAndALongerLineIsRejectedWithItsPosition()
    {
        var atCap = new string('a', ArchiveLines.MaxLineChars);
        var lines = await ReadAllAsync("{}\r\n\n" + atCap + "\n{\"x\":1}");
        Assert.Equal(["{}", "", atCap, "{\"x\":1}"], lines);

        var error = await Assert.ThrowsAsync<ExportArchiveException>(() => ReadAllAsync("{}\n" + atCap + "a\n"));
        Assert.StartsWith("data/projects/p/records.jsonl:2:", error.Message);
    }

    private static async Task<List<string>> ReadAllAsync(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var lines = new List<string>();
        await foreach (var line in ArchiveLines.ReadAsync(stream, "data/projects/p/records.jsonl", CancellationToken.None))
        {
            lines.Add(line);
        }

        return lines;
    }
}
