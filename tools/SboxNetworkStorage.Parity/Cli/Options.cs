namespace SboxNetworkStorage.Parity.Cli;

/// <summary><c>--name value</c> options and <c>--flag</c> switches after the mode word.</summary>
public sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public Options(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                throw new ParityException($"unexpected argument '{arg}'");
            }

            var name = arg[2..];
            var equals = name.IndexOf('=');
            if (equals > 0)
            {
                _values[name[..equals]] = name[(equals + 1)..];
            }
            else if (i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                _values[name] = list[++i];
            }
            else
            {
                _values[name] = "true";
            }
        }
    }

    public string Required(string name) => Optional(name) ?? throw new ParityException($"--{name} is required");

    public string? Optional(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var value) ? value : null;
    }

    public string Optional(string name, string fallback) => Optional(name) ?? fallback;

    /// <summary>Rejects options the mode did not read (catches typos such as <c>--corpse</c>).</summary>
    public void RejectUnknown()
    {
        var unknown = _values.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ParityException($"unknown option(s): {string.Join(", ", unknown.Select(u => "--" + u))}");
        }
    }

    /// <summary>
    /// Resolves a path relative to the working directory, falling back to the
    /// repository root so defaults like <c>tests/parity/corpus</c> work from anywhere.
    /// </summary>
    public static string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path) || File.Exists(path) || Directory.Exists(path))
        {
            return Path.GetFullPath(path);
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SboxNetworkStorage.sln")))
            {
                return Path.Combine(dir.FullName, path);
            }
        }

        return Path.GetFullPath(path);
    }
}
