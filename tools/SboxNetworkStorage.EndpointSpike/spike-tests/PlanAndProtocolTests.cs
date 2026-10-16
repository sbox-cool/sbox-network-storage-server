using System.Text;
using SboxNetworkStorage.EndpointSpike.Plan;
using SboxNetworkStorage.EndpointSpike.Sandbox;

namespace SboxNetworkStorage.EndpointSpike.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Key_insertion_order_does_not_change_the_serialized_form_or_hash()
    {
        var forward = new Dictionary<string, object?> { ["a"] = 1.0, ["b"] = "x" };
        var reverse = new Dictionary<string, object?> { ["b"] = "x", ["a"] = 1.0 };

        Assert.Equal(CanonicalJson.Serialize(forward), CanonicalJson.Serialize(reverse));
        Assert.Equal(CanonicalJson.Sha256Hex(CanonicalJson.Serialize(forward)),
            CanonicalJson.Sha256Hex(CanonicalJson.Serialize(reverse)));
    }

    [Fact]
    public void Sha256_hex_is_64_lowercase_hex_characters()
    {
        var digest = CanonicalJson.Sha256Hex("ns-endpoint/1");

        Assert.Matches("^[0-9a-f]{64}$", digest);
    }
}

public sealed class PlanReaderTests
{
    [Fact]
    public void Plan_json_round_trips_through_the_value_model_without_loss()
    {
        const string json = """{"list":[1,2.5,"s",true,null],"nested":{"k":"v"}}""";

        var parsed = PlanReader.ParseObject(json);
        var reserialized = CanonicalJson.Serialize(parsed);

        Assert.Equal(CanonicalJson.Serialize(PlanReader.ParseObject(reserialized)), reserialized);
        Assert.Equal("v", ((Dictionary<string, object?>)parsed["nested"]!)["k"]);
    }

    [Fact]
    public void Non_object_plan_root_is_rejected()
    {
        Assert.ThrowsAny<Exception>(() => PlanReader.ParseObject("[1,2]"));
    }
}

public sealed class BoundedLineReaderTests
{
    private static BoundedLineReader Reader(string text, int max) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

    [Fact]
    public async Task Line_exactly_at_the_ceiling_is_returned()
    {
        var reader = Reader(new string('a', 16) + "\n", 16);

        Assert.Equal(new string('a', 16), await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Line_one_byte_over_the_ceiling_is_rejected_before_its_newline()
    {
        var reader = Reader(new string('a', 17) + "\n", 16);

        await Assert.ThrowsAsync<ProtocolLimitException>(() => reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_line_longer_than_one_read_chunk_but_under_the_ceiling_is_reassembled()
    {
        var payload = new string('z', 150_000);
        var reader = Reader(payload + "\n", 1 << 20);

        Assert.Equal(payload, await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Crlf_terminator_is_stripped_and_lines_come_back_in_order()
    {
        var reader = Reader("one\r\ntwo\nthree", 64);

        Assert.Equal("one", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("two", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Equal("three", await reader.ReadLineAsync(CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Flood_without_newline_is_cut_off_at_the_ceiling()
    {
        var reader = new BoundedLineReader(new EndlessStream(), 4096);

        await Assert.ThrowsAsync<ProtocolLimitException>(() => reader.ReadLineAsync(CancellationToken.None));
    }

    /// <summary>Produces bytes forever with no newline, to prove the reader stops at its ceiling.</summary>
    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
