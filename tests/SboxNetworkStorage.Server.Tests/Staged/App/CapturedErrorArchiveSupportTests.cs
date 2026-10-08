using SboxNetworkStorage.Application.Errors;
using SboxNetworkStorage.Contracts.Errors;

namespace SboxNetworkStorage.Server.Tests.Errors;

public sealed class CapturedErrorArchiveSupportTests
{
    [Fact]
    public void NormalizeForStorage_TruncatesLongStackAndMessage()
    {
        var error = new CapturedErrorDto(
            "not-a-guid",
            DateTimeOffset.UtcNow,
            ".NET backend",
            "GET",
            "/boom",
            500,
            "InvalidOperationException",
            "corr-1",
            new string('m', CapturedErrorArchiveSupport.MaxMessageLength + 10),
            new string('s', CapturedErrorArchiveSupport.MaxStackLength + 10));

        var normalized = CapturedErrorArchiveSupport.NormalizeForStorage(error);

        Assert.True(Guid.TryParse(normalized.Id, out _));
        Assert.Equal(CapturedErrorArchiveSupport.MaxMessageLength, normalized.Message.Length);
        Assert.Equal(CapturedErrorArchiveSupport.MaxStackLength, normalized.StackTrace!.Length);
    }

    [Fact]
    public void ForList_StripsStackTrace()
    {
        var error = new CapturedErrorDto(
            Guid.NewGuid().ToString("D"),
            DateTimeOffset.UtcNow,
            ".NET backend",
            "GET",
            "/boom",
            500,
            "InvalidOperationException",
            "corr-1",
            "boom",
            "stack body");

        var listed = CapturedErrorArchiveSupport.ForList(error);

        Assert.Null(listed.StackTrace);
    }

    [Fact]
    public void RedactSensitiveText_MasksSecretsForDiscord()
    {
        var redacted = CapturedErrorArchiveSupport.RedactSensitiveText(
            "secret=test-password=should-not-leak Authorization: Bearer abc.def.ghi postgres://user:pass@host/db");

        Assert.Contains("[REDACTED]", redacted);
        Assert.DoesNotContain("should-not-leak", redacted);
        Assert.DoesNotContain("abc.def.ghi", redacted);
        Assert.DoesNotContain("user:pass", redacted);
    }

    [Fact]
    public void BuildDiscordStackFields_SplitsLongStacksIntoChunks()
    {
        var stack = string.Join('\n', Enumerable.Range(0, 200).Select(index => $"at Example.Type.Method{index}()"));
        var fields = CapturedErrorArchiveSupport.BuildDiscordStackFields(stack);

        Assert.NotEmpty(fields);
        Assert.True(fields.Count <= CapturedErrorArchiveSupport.DiscordStackFieldLimit);
        Assert.All(fields, field => Assert.True(field.Value.Length <= CapturedErrorArchiveSupport.DiscordEmbedFieldValueLimit + 8));
    }
}
