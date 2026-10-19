namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// The vendored monaco-editor build (<c>wwwroot/vendor/monaco</c>), served from embedded resources. The server project
/// embeds each file as <c>SboxNetworkStorage.Server.Owner.vendor-monaco.&lt;dir&gt;.&lt;file&gt;</c>, with the directory
/// separators replaced by dots (see SboxNetworkStorage.Server.csproj). Only embedded files can be served, so no path
/// reaches the file system.
/// </summary>
internal static class OwnerMonacoAssets
{
    private const string ResourcePrefix = "SboxNetworkStorage.Server.Owner.vendor-monaco.";

    /// <summary>
    /// The embedded file at a path relative to the Monaco root, for example <c>vs/loader.js</c>, with its content type.
    /// Returns null when the path is not a plain relative path, the extension is not served, or no such file was embedded.
    /// </summary>
    public static (Stream Stream, string ContentType)? Open(string relativePath)
    {
        if (!IsPlainRelativePath(relativePath) || ContentTypeFor(relativePath) is not { } contentType)
            return null;
        var stream = typeof(OwnerMonacoAssets).Assembly.GetManifestResourceStream(ResourcePrefix + relativePath.Replace('/', '.'));
        return stream is null ? null : (stream, contentType);
    }

    /// <summary>
    /// True for one or more '/'-separated segments of ASCII letters, digits, '.', '-' and '_', none of them empty, "." or "..".
    /// Rejects rooted paths, backslashes, drive letters, control characters and traversal.
    /// </summary>
    internal static bool IsPlainRelativePath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return false;
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment is "" or "." or "..")
                return false;
            foreach (var c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
                    return false;
            }
        }
        return true;
    }

    /// <summary>The content type for a served file extension, or null when the extension is not served.</summary>
    internal static string? ContentTypeFor(string relativePath) => Path.GetExtension(relativePath) switch
    {
        ".js" => "text/javascript",
        ".css" => "text/css",
        ".txt" => "text/plain; charset=utf-8",
        _ => null,
    };
}
