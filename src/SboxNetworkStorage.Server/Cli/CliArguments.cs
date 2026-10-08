namespace SboxNetworkStorage.Server.Cli;

/// <summary>Minimal, dependency-free parser: positional words plus <c>--name value</c> / <c>--flag</c> options.</summary>
public sealed class CliArguments
{
    private static readonly HashSet<string> BooleanFlags = new(StringComparer.Ordinal)
    {
        "non-interactive", "check", "follow", "f", "help", "h", "json", "yes", "y", "show-secrets", "force", "no-secrets", "config", "remove",
        "accept-letsencrypt-terms", "auto", "all-instances", "auto-update", "verify-only"
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Positionals { get; }

    private CliArguments(List<string> positionals) => Positionals = positionals;

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var positionals = new List<string>();
        var parsed = new CliArguments(positionals);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--")
            {
                positionals.AddRange(args.Skip(i + 1));
                break;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal) || (arg.Length == 2 && arg[0] == '-' && char.IsLetter(arg[1])))
            {
                var name = arg.TrimStart('-');
                string? value = null;
                var equals = name.IndexOf('=');
                if (equals >= 0)
                {
                    value = name[(equals + 1)..];
                    name = name[..equals];
                }

                if (value is null && !BooleanFlags.Contains(name) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    value = args[++i];
                }

                if (value is null)
                {
                    parsed._flags.Add(name);
                }
                else
                {
                    parsed._options[name] = value;
                }

                continue;
            }

            positionals.Add(arg);
        }

        return parsed;
    }

    public string? Option(string name) => _options.GetValueOrDefault(name);

    public bool Flag(params string[] names) => names.Any(_flags.Contains);

    public string? Positional(int index) => index < Positionals.Count ? Positionals[index] : null;

    public IReadOnlyDictionary<string, string> Options => _options;
}
