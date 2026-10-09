using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>Writes commented config files and edits single keys without disturbing comments.</summary>
public static partial class ConfigFiles
{
    private static readonly IReadOnlyDictionary<string, string> FileHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [SettingDefinitions.ServerFile] = "Listener, TLS, logging and auth settings.",
        [SettingDefinitions.DatabaseFile] = "Database backend. Use `sbox-ns db test` after editing.",
        [SettingDefinitions.UpdatesFile] = "Update notices and opt-in unattended updates (auto_install, off by default). Manual: `sbox-ns update`.",
        [SettingDefinitions.AlertsFile] = "Operator alerts (Discord webhook, SMTP email) for captured errors. Disabled by default.",
    };

    /// <summary>Renders a complete, commented config file. <paramref name="values"/> overrides defaults by key.</summary>
    public static string Render(string file, IReadOnlyDictionary<string, object>? values = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# sbox-ns {file}");
        builder.AppendLine($"# {FileHeaders[file]}");
        builder.AppendLine("# Edit this file, then restart the server (`sbox-ns service restart`). Check it with `sbox-ns config validate`.");
        builder.AppendLine("# Every key can also be set with an environment variable, e.g. NS_SERVER__LISTEN.");

        foreach (var table in SettingDefinitions.All.Where(d => d.File == file).GroupBy(d => d.Table))
        {
            builder.AppendLine();
            builder.AppendLine($"[{table.Key}]");
            foreach (var definition in table)
            {
                builder.AppendLine($"# {definition.Description}");
                if (definition.AllowedValues is not null)
                {
                    builder.AppendLine($"# Allowed: {string.Join(", ", definition.AllowedValues)}");
                }

                var value = values is not null && values.TryGetValue(definition.Key, out var overridden) ? overridden : definition.DefaultValue;
                builder.AppendLine($"{definition.Name} = {FormatValue(value)}");
            }
        }

        return builder.ToString();
    }

    /// <summary>Writes any missing config files with defaults (plus <paramref name="values"/>); existing files are kept.</summary>
    public static IReadOnlyList<string> WriteMissing(string configDirectory, IReadOnlyDictionary<string, object>? values = null)
    {
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(configDirectory, ConfigLoader.ConfDirectory));
        var written = new List<string>();
        foreach (var file in SettingDefinitions.Files)
        {
            var path = Path.Combine(configDirectory, file);
            if (File.Exists(path))
            {
                continue;
            }

            WriteAtomically(path, Render(file, values));
            written.Add(path);
        }

        return written;
    }

    /// <summary>Overwrites all config files with defaults plus <paramref name="values"/>.</summary>
    public static void WriteAll(string configDirectory, IReadOnlyDictionary<string, object> values)
    {
        Directory.CreateDirectory(configDirectory);
        Directory.CreateDirectory(Path.Combine(configDirectory, ConfigLoader.ConfDirectory));
        foreach (var file in SettingDefinitions.Files)
        {
            WriteAtomically(Path.Combine(configDirectory, file), Render(file, values));
        }
    }

    /// <summary>
    /// Sets one key in the file that owns it, replacing the existing assignment line
    /// in its table (or adding it) while leaving every other line untouched.
    /// </summary>
    public static string SetValue(string configDirectory, SettingDefinition definition, object value)
    {
        var path = Path.Combine(configDirectory, definition.File);
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            var document = Toml.Parse(text, path);
            if (document.HasErrors)
            {
                throw new InvalidOperationException($"Cannot edit invalid TOML in {path}; fix it with config edit first.");
            }

            var table = document.Tables.FirstOrDefault(t => t.Name?.ToString().Trim() == definition.Table);
            var item = table?.Items.FirstOrDefault(kv => kv.Key?.ToString().Trim() == definition.Name);
            if (item?.Value is { } existingValue)
            {
                var span = existingValue.Span;
                WriteAtomically(path, text[..span.Offset] + FormatValue(value) + text[(span.Offset + span.Length)..]);
                return path;
            }
        }

        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : Render(definition.File).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var assignment = $"{definition.Name} = {FormatValue(value)}";

        var tableStart = lines.FindIndex(l => TableHeader().Match(l) is { Success: true } m && m.Groups[1].Value.Trim() == definition.Table);
        if (tableStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{definition.Table}]");
            lines.Add(assignment);
        }
        else
        {
            var tableEnd = lines.FindIndex(tableStart + 1, l => TableHeader().IsMatch(l));
            if (tableEnd < 0)
            {
                tableEnd = lines.Count;
            }

            var keyPattern = new Regex($@"^\s*{Regex.Escape(definition.Name)}\s*=");
            var existing = lines.FindIndex(tableStart + 1, tableEnd - tableStart - 1, l => keyPattern.IsMatch(l));
            if (existing >= 0)
            {
                lines[existing] = assignment;
            }
            else
            {
                var insertAt = tableEnd;
                while (insertAt > tableStart + 1 && string.IsNullOrWhiteSpace(lines[insertAt - 1]))
                {
                    insertAt--;
                }

                lines.Insert(insertAt, assignment);
            }
        }

        WriteAtomically(path, string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine);
        return path;
    }

    public static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => QuoteString(s),
        _ => throw new ArgumentException($"Unsupported config value type {value.GetType().Name}", nameof(value))
    };

    private static string QuoteString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:X4}",
                _ => c.ToString()
            });
        }

        return builder.Append('"').ToString();
    }

    /// <summary>Writes via a temp file + rename so a crash never leaves a half-written file.</summary>
    public static void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex(@"^\s*\[\s*([A-Za-z0-9_.\-]+)\s*\]\s*(#.*)?$", RegexOptions.None, 100)]
    private static partial Regex TableHeader();
}
