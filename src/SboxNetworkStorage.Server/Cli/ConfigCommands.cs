using System.Diagnostics;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Cli;

public static class ConfigCommands
{
    public static int Run(CliContext context)
    {
        var sub = context.RequirePositional(1, "path|show|get|set|validate|edit");
        return sub switch
        {
            "path" => Path(context),
            "show" => Show(context),
            "get" => Get(context),
            "set" => Set(context),
            "validate" => Validate(context),
            "edit" => Edit(context),
            _ => throw new CliException($"unknown config command '{sub}'", CliApp.Usage)
        };
    }

    private static int Path(CliContext context)
    {
        Console.WriteLine(context.LoadConfig().ConfigDirectory);
        return CliApp.Ok;
    }

    private static int Show(CliContext context)
    {
        var config = context.LoadConfig();
        var showSecrets = context.Args.Flag("show-secrets");
        Console.WriteLine($"# config folder: {config.ConfigDirectory}");
        Console.WriteLine($"# data folder:   {config.DataDirectory}");
        foreach (var file in SettingDefinitions.Files)
        {
            Console.WriteLine();
            Console.WriteLine($"# {file}");
            foreach (var definition in SettingDefinitions.All.Where(d => d.File == file))
            {
                var value = config.Values[definition.Key];
                var display = definition.Secret && !showSecrets && value.Value is string s && s.Length > 0
                    ? "\"********\""
                    : ConfigFiles.FormatValue(value.Value);
                var origin = value.Source == SettingSource.Default ? "default" : value.Origin;
                Console.WriteLine($"{definition.Key} = {display}    # {origin}");
            }
        }

        PrintIssues(config);
        return config.IsValid ? CliApp.Ok : CliApp.Usage;
    }

    private static int Get(CliContext context)
    {
        var key = context.RequirePositional(2, "key");
        var definition = SettingDefinitions.Find(key) ?? throw new CliException($"unknown setting '{key}'", CliApp.Usage);
        var config = context.LoadConfig();
        var value = config.Values[definition.Key].Value;
        // Same redaction as `config show`: this output ends up in terminals, logs and agent transcripts.
        if (definition.Secret && !context.Args.Flag("show-secrets") && value is string { Length: > 0 })
        {
            Console.WriteLine("********");
            return CliApp.Ok;
        }

        Console.WriteLine(value is string text ? text : ConfigFiles.FormatValue(value));
        return CliApp.Ok;
    }

    private static int Set(CliContext context)
    {
        var key = context.RequirePositional(2, "key");
        var raw = context.RequirePositional(3, "value");
        var definition = SettingDefinitions.Find(key) ?? throw new CliException($"unknown setting '{key}'", CliApp.Usage);
        if (!ConfigLoader.TryConvert(definition, raw, out var value, out var error))
        {
            throw new CliException(error, CliApp.Usage);
        }

        var configDirectory = ConfigPaths.ResolveConfigDirectory(context.Args.Option("config-dir"));
        string file;
        try
        {
            file = ConfigFiles.SetValue(configDirectory, definition, value);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ex.Message, CliApp.Usage);
        }
        Console.WriteLine($"Set {key} in {file}. Restart the server to apply: sbox-ns service restart");

        var after = context.LoadConfig();
        if (after.Values[key].Source is SettingSource.ConfD or SettingSource.Environment or SettingSource.Flag)
        {
            Console.WriteLine($"Note: {key} is overridden by {after.Values[key].Origin}, so the new value is not in effect.");
        }

        PrintIssues(after);
        return after.IsValid ? CliApp.Ok : CliApp.Usage;
    }

    private static int Validate(CliContext context)
    {
        var config = context.LoadConfig();
        if (config.IsValid)
        {
            Console.WriteLine($"Configuration in {config.ConfigDirectory} is valid ({config.LoadedFiles.Count} file(s) loaded).");
            return CliApp.Ok;
        }

        PrintIssues(config);
        return CliApp.Usage;
    }

    private static int Edit(CliContext context)
    {
        var configDirectory = ConfigPaths.ResolveConfigDirectory(context.Args.Option("config-dir"));
        var fileName = context.Args.Positional(2) ?? SettingDefinitions.ServerFile;
        if (!fileName.EndsWith(".toml", StringComparison.Ordinal))
        {
            fileName += ".toml";
        }

        ConfigFiles.WriteMissing(configDirectory);
        var path = System.IO.Path.Combine(configDirectory, fileName);
        var backup = File.Exists(path) ? File.ReadAllText(path) : null;
        var editor = Environment.GetEnvironmentVariable("VISUAL")
            ?? Environment.GetEnvironmentVariable("EDITOR")
            ?? (OperatingSystem.IsWindows() ? "notepad" : "nano");

        while (true)
        {
            using (var process = Process.Start(new ProcessStartInfo(editor, $"\"{path}\"") { UseShellExecute = false }))
            {
                process?.WaitForExit();
            }

            var config = context.LoadConfig();
            if (config.IsValid)
            {
                Console.WriteLine($"Saved {path}. Restart the server to apply: sbox-ns service restart");
                return CliApp.Ok;
            }

            PrintIssues(config);
            Console.Write("Configuration is invalid. [e]dit again or [r]evert? ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is "r" or "revert" or null)
            {
                if (backup is null)
                {
                    File.Delete(path);
                }
                else
                {
                    ConfigFiles.WriteAtomically(path, backup);
                }

                Console.WriteLine($"Reverted {path}.");
                return CliApp.Usage;
            }
        }
    }

    internal static void PrintIssues(EffectiveConfig config)
    {
        if (config.IsValid)
        {
            return;
        }

        Console.Error.WriteLine($"{config.Issues.Count} configuration problem(s):");
        foreach (var issue in config.Issues)
        {
            Console.Error.WriteLine($"  {issue}");
        }
    }
}
