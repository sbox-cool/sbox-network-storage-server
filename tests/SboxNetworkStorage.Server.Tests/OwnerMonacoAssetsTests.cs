using SboxNetworkStorage.Server.Owner;
using Xunit;

namespace SboxNetworkStorage.Server.Tests;

/// <summary>The path guard for <c>/owner-assets/vendor/monaco/{path}</c> and the embedded build it serves.</summary>
public sealed class OwnerMonacoAssetsTests
{
    [Theory]
    [InlineData("vs/loader.js")]
    [InlineData("vs/editor/editor.main.js")]
    [InlineData("vs/editor/editor.main.css")]
    [InlineData("vs/assets/ts.worker-g3pjEjl4.js")]
    [InlineData("LICENSE.txt")]
    public void PlainRelativePathsAreAccepted(string path) =>
        Assert.True(OwnerMonacoAssets.IsPlainRelativePath(path));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/vs/loader.js")]
    [InlineData("vs//loader.js")]
    [InlineData("vs/loader.js/")]
    [InlineData("../appsettings.json")]
    [InlineData("vs/../../secret.txt")]
    [InlineData("vs/./loader.js")]
    [InlineData("vs\\loader.js")]
    [InlineData("C:/loader.js")]
    [InlineData("vs/loader.js\0")]
    [InlineData("vs/loader%2Ejs")]
    [InlineData("vs/loader js")]
    [InlineData("vs/loader.js?x=1")]
    public void TraversalRootedAndMalformedPathsAreRejected(string? path)
    {
        Assert.False(OwnerMonacoAssets.IsPlainRelativePath(path));
        Assert.Null(OwnerMonacoAssets.Open(path ?? string.Empty));
    }

    [Fact]
    public void EmbeddedFileOpensWithItsContentType()
    {
        var asset = OwnerMonacoAssets.Open("vs/loader.js");
        Assert.NotNull(asset);
        using var stream = asset.Value.Stream;
        Assert.Equal("text/javascript", asset.Value.ContentType);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void RootFileOpensAsPlainText()
    {
        var asset = OwnerMonacoAssets.Open("LICENSE.txt");
        Assert.NotNull(asset);
        using var stream = asset.Value.Stream;
        Assert.Equal("text/plain; charset=utf-8", asset.Value.ContentType);
    }

    [Theory]
    [InlineData("vs/missing.js")]
    [InlineData("vs/loader.html")]
    [InlineData("vs/editor/editor.worker.js")]
    public void MissingUnservedAndExcludedFilesAreNotOpened(string path) =>
        Assert.Null(OwnerMonacoAssets.Open(path));
}
