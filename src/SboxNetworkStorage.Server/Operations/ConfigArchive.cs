using System.Text;
using SboxNetworkStorage.Server.Configuration;
using Tomlyn;
using Tomlyn.Syntax;

namespace SboxNetworkStorage.Server.Operations;

/// <summary>
/// Copies the config folder into an export (server/database/updates/alerts.toml,
/// conf.d/*.toml, and optionally secrets) and restores it on import with atomic writes.
/// In the state layout the runtime files (generated secrets, tunnel identity and token, managed overlays)
/// are read from and restored into the state folder; archive entry names are the same in both layouts.
/// </summary>
public static class ConfigArchive
{
    public const string SecretsDirectory = "secrets";

    /// <summary>On import the target keeps its own database.toml; the archived one is saved beside it under this name.</summary>
    public const string ImportedDatabaseFile = "database.toml.from-export";

    public const string BackupSuffix = ".before-import";

    /// <summary>Settings whose value is a path to a file holding secret material.</summary>
    private static readonly string[] SecretFileSettings =
    [
        "auth.session_secret_file", "auth.storage_encryption_key_file", "auth.security_signing_key_file",
        "database.postgres.password_file", "alerts.discord.webhook_url_file", "alerts.smtp.password_file",
    ];

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerOnlyDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Outcome of <see cref="Stage"/>: archive entry names, plus referenced secret files that live outside the config folder.</summary>
    public sealed record StagedConfig(IReadOnlyList<string> Entries, IReadOnlyList<string> SecretFilesOutsideConfig);

    /// <summary>
    /// Copies the config folder into <c>&lt;stagingRoot&gt;/config/</c>. Without
    /// <paramref name="includeSecrets"/> the secrets folder and secret files are skipped
    /// and inline secret values (passwords, webhook URLs, connection strings) are blanked.
    /// </summary>
    public static StagedConfig Stage(EffectiveConfig config, bool includeSecrets, string stagingRoot)
    {
        var configDirectory = Path.GetFullPath(config.ConfigDirectory);
        var runtimeDirectory = Path.GetFullPath(config.RuntimeDirectory);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in SettingDefinitions.Files.Where(f => File.Exists(Path.Combine(configDirectory, f))))
        {
            files[file] = Path.Combine(configDirectory, file);
        }

        foreach (var folder in new[] { Path.Combine(configDirectory, ConfigLoader.ConfDirectory), Path.Combine(runtimeDirectory, ConfigLoader.ConfDirectory) })
        {
            if (Directory.Exists(folder))
            {
                foreach (var path in Directory.GetFiles(folder, "*.toml"))
                {
                    files[$"{ConfigLoader.ConfDirectory}/{Path.GetFileName(path)}"] = path;
                }
            }
        }

        var outside = new List<string>();
        if (includeSecrets)
        {
            foreach (var root in new[] { configDirectory, runtimeDirectory }.Distinct(StringComparer.Ordinal))
            {
                var secrets = Path.Combine(root, SecretsDirectory);
                if (!Directory.Exists(secrets))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(secrets, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(path);
                    if (!fileName.EndsWith(BackupSuffix, StringComparison.Ordinal) && !(fileName.StartsWith('.') && fileName.EndsWith(".tmp", StringComparison.Ordinal)))
                    {
                        files[RelativeEntry(root, path)] = path;
                    }
                }
            }

            foreach (var setting in SecretFileSettings)
            {
                var path = config.GetPath(setting, IsRuntimeSetting(setting) ? runtimeDirectory : configDirectory);
                if (path.Length == 0 || !File.Exists(path))
                {
                    continue;
                }

                if (IsInside(configDirectory, path))
                {
                    files[RelativeEntry(configDirectory, path)] = path;
                }
                else if (IsInside(runtimeDirectory, path))
                {
                    files[RelativeEntry(runtimeDirectory, path)] = path;
                }
                else
                {
                    outside.Add(path);
                }
            }
        }

        var entries = new List<string>();
        foreach (var (relative, source) in files)
        {
            var target = Path.Combine(stagingRoot, ExportFormat.ConfigPrefix.TrimEnd('/'), relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!includeSecrets && relative.EndsWith(".toml", StringComparison.Ordinal))
            {
                File.WriteAllText(target, RedactInlineSecrets(File.ReadAllText(source), relative), new UTF8Encoding(false));
            }
            else
            {
                File.Copy(source, target);
            }

            entries.Add(ExportFormat.ConfigPrefix + relative);
        }

        return new StagedConfig(entries, outside);
    }

    /// <summary>The generated-secret settings resolve against the runtime folder; the other secret files are operator-provided.</summary>
    private static bool IsRuntimeSetting(string setting) => setting.StartsWith("auth.", StringComparison.Ordinal);

    /// <summary>True when an archive entry belongs in the state folder: a managed overlay, the tunnel identity or token, or a generated secret file.</summary>
    private static bool IsRuntimeEntry(EffectiveConfig config, string relative)
    {
        if (config.Layout == ConfigLayout.Legacy)
        {
            return false;
        }

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith(ConfigLoader.ConfDirectory + "/", StringComparison.Ordinal))
        {
            return StateLayout.ManagedOverlayFiles.Contains(normalized[(ConfigLoader.ConfDirectory.Length + 1)..], StringComparer.Ordinal);
        }

        if (normalized is $"{SecretsDirectory}/{StateLayout.TunnelIdentityFile}" or $"{SecretsDirectory}/{StateLayout.TunnelTokenFile}")
        {
            return true;
        }

        return SecretFileSettings.Where(IsRuntimeSetting).Select(config.GetString)
            .Any(value => value.Length > 0 && !Path.IsPathRooted(value) && Path.GetRelativePath(".", value).Replace('\\', '/') == normalized);
    }

    /// <summary>Blanks every non-empty inline value of a setting marked secret.</summary>
    public static string RedactInlineSecrets(string text, string displayName)
    {
        var document = Toml.Parse(text, displayName);
        if (document.HasErrors)
        {
            throw new ExportArchiveException($"cannot remove secrets from {displayName}: it is not valid TOML. Run `sbox-ns config validate`.");
        }

        var spans = new List<SourceSpan>();
        void Visit(string? table, KeyValueSyntax item)
        {
            var leaf = KeyText(item.Key);
            var key = string.IsNullOrEmpty(table) ? leaf : $"{table}.{leaf}";
            if (SettingDefinitions.Find(key) is { Secret: true } && item.Value is StringValueSyntax { Value.Length: > 0 } value)
            {
                spans.Add(value.Span);
            }
        }

        foreach (var item in document.KeyValues)
        {
            Visit(null, item);
        }

        foreach (var table in document.Tables)
        {
            foreach (var item in table.Items)
            {
                Visit(KeyText(table.Name), item);
            }
        }

        var builder = new StringBuilder(text);
        foreach (var span in spans.OrderByDescending(s => s.Offset))
        {
            builder.Remove(span.Offset, span.Length).Insert(span.Offset, "\"\"");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Writes archived config files into the config folder. TOML files go through
    /// <see cref="ConfigFiles.WriteAtomically"/>; everything else (secret material) is
    /// written atomically with owner-only permissions. Changed files are kept as
    /// <c>*.before-import</c>. database.toml is never replaced: the import already
    /// targeted the configured database, so the archived copy is saved as
    /// <see cref="ImportedDatabaseFile"/> for reference.
    /// </summary>
    public static IReadOnlyList<string> Restore(EffectiveConfig config, IReadOnlyDictionary<string, byte[]> files)
    {
        var targets = new List<(string Relative, byte[] Content, string Target, bool Runtime)>();
        foreach (var (relative, content) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var runtime = IsRuntimeEntry(config, relative);
            var root = Path.GetFullPath(runtime ? config.RuntimeDirectory : config.ConfigDirectory);
            var targetRelative = relative == SettingDefinitions.DatabaseFile ? ImportedDatabaseFile : relative;
            var target = Path.GetFullPath(Path.Combine(root, targetRelative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsInside(root, target))
            {
                throw new ExportArchiveException($"Archive config entry '{relative}' points outside the config folder.");
            }

            RejectReparsePoints(root, target);
            RejectReparsePoints(root, target + BackupSuffix);
            targets.Add((relative, content, target, runtime));
        }

        var written = new List<string>();
        foreach (var (relative, content, target, runtime) in targets)
        {
            // State files are written as the service account so the server can read them afterwards.
            using var identity = runtime ? RuntimeIdentity.Enter(config) : null;
            if (runtime)
            {
                config.EnsureRuntimeDirectory();
            }


            if (File.Exists(target))
            {
                if (File.ReadAllBytes(target).AsSpan().SequenceEqual(content))
                {
                    continue;
                }

                File.Copy(target, target + BackupSuffix, overwrite: true);
            }

            if (relative.EndsWith(".toml", StringComparison.Ordinal))
            {
                ConfigFiles.WriteAtomically(target, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content));
            }
            else
            {
                WriteOwnerOnlyAtomically(target, content);
            }

            written.Add(target);
        }

        return written;
    }

    private static void RejectReparsePoints(string root, string target)
    {
        var path = target;
        while (true)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new ExportArchiveException($"Cannot restore config through symbolic link or reparse point '{path}'.");
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                break;
            }

            path = Path.GetDirectoryName(path)!;
        }
    }

    private static void WriteOwnerOnlyAtomically(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, OwnerOnlyDirectory);
            }
        }

        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = OwnerOnlyFile;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static bool IsInside(string directory, string path) => StateLayout.IsInside(directory, path);

    private static string RelativeEntry(string directory, string path)
        => Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');

    private static string KeyText(KeySyntax? key)
        => key is null ? string.Empty : string.Join('.', key.ToString().Split('.').Select(part => part.Trim().Trim('"', '\'')));
}
