using SboxNetworkStorage.Server.Operations;
using SboxNetworkStorage.Storage.Relational;

namespace SboxNetworkStorage.Server.Cli;

/// <summary><c>sbox-ns export</c> / <c>sbox-ns import</c>: move a whole server, or switch database drivers, with two commands.</summary>
public static class ExportCommands
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static async Task<int> ExportAsync(CliContext context)
    {
        var includeSecrets = !context.Args.Flag("no-secrets");
        var config = context.LoadValidConfig();
        var target = Path.GetFullPath(context.Args.Option("out")
            ?? Path.Combine(config.DataDirectory, "exports", ExportFormat.DefaultFileName(DateTimeOffset.UtcNow)));
        if (File.Exists(target))
        {
            throw new CliException($"{target} already exists; choose another --out path.");
        }

        await using var services = CliServices.Build(config);
        var store = services.GetRequiredService<INetworkStorageStore>();
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        await admin.MigrateAsync(CancellationToken.None);

        StagedExport staged;
        try
        {
            staged = await ServerArchive.PrepareExportAsync(store, admin, config, includeSecrets, CancellationToken.None);
        }
        catch (ExportArchiveException ex)
        {
            throw new CliException(ex.Message);
        }

        await using (staged)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = OwnerOnly;
            }

            FileStream output;
            try
            {
                output = new FileStream(target, options);
            }
            catch (IOException) when (File.Exists(target))
            {
                throw new CliException($"{target} already exists; choose another --out path.");
            }

            try
            {
                await using (output)
                {
                    await staged.WriteToAsync(output, CancellationToken.None);
                }
            }
            catch
            {
                File.Delete(target);
                throw;
            }

            var manifest = staged.Manifest;
            Console.WriteLine($"Export written to {target}");
            Console.WriteLine($"  {manifest.Projects.Count} project(s), {manifest.TotalRows} row(s) from {manifest.Provider} (schema {manifest.SchemaVersion})");
            foreach (var project in manifest.Projects)
            {
                Console.WriteLine($"  {project.Id}: {string.Join(", ", project.Counts.Select(c => $"{c.Key} {c.Value}"))}");
            }

            if (includeSecrets)
            {
                Console.WriteLine();
                Console.WriteLine("WARNING: this archive contains the server's SECRETS (secrets/ folder and secret settings):");
                Console.WriteLine("         anyone holding it can forge player sessions and sign security configs.");
                Console.WriteLine("         It was created readable by you only (0600). Keep it private; delete it after use.");
                Console.WriteLine("         Use --no-secrets for an archive you can share or store less carefully.");
                foreach (var outside in staged.SecretFilesOutsideConfig)
                {
                    Console.WriteLine($"         Not included (outside the config folder): {outside}");
                }
            }
            else
            {
                Console.WriteLine("Secrets were left out (--no-secrets). Secret API keys only keep working on a server that has this server's secrets/ folder.");
            }

            Console.WriteLine($"Restore anywhere: sbox-ns import {target} [--config]");
        }

        return CliApp.Ok;
    }

    public static async Task<int> ImportAsync(CliContext context)
    {
        var path = Path.GetFullPath(context.RequirePositional(1, "archive"));
        if (!File.Exists(path))
        {
            throw new CliException($"{path} does not exist.");
        }

        var options = new ImportOptions(Force: context.Args.Flag("force"), RestoreConfig: context.Args.Flag("config"));
        var config = context.LoadValidConfig();
        Console.WriteLine("Stop the server before importing (sbox-ns service stop).");

        await using var services = CliServices.Build(config);
        var store = services.GetRequiredService<INetworkStorageStore>();
        var admin = services.GetRequiredService<INetworkStorageStoreAdmin>();
        await admin.MigrateAsync(CancellationToken.None);

        ImportResult result;
        try
        {
            await using var archive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            result = await ServerArchive.ImportAsync(archive, store, config, options, CancellationToken.None);
        }
        catch (ExportArchiveException ex)
        {
            throw new CliException(ex.Message);
        }

        var manifest = result.Manifest;
        Console.WriteLine($"Imported {result.RowsApplied} row(s) for {manifest.Projects.Count} project(s) into {admin.ProviderName} at {admin.RedactedTarget}");
        Console.WriteLine($"  from {manifest.Provider} export created {manifest.CreatedAt:u} by sbox-ns {manifest.SboxNsVersion}");
        if (options.RestoreConfig)
        {
            foreach (var written in result.ConfigFilesWritten)
            {
                Console.WriteLine($"  config: wrote {written}");
            }

            if (result.ConfigFilesWritten.Count == 0)
            {
                Console.WriteLine("  config: already identical, nothing written");
            }

            Console.WriteLine($"  database.toml was kept; the archived copy (if any) is {Path.Combine(config.ConfigDirectory, ConfigArchive.ImportedDatabaseFile)}.");
            Console.WriteLine($"  Replaced files were kept as *{ConfigArchive.BackupSuffix}. Review server.toml (listen, public_url, TLS) for this machine.");
            if (!manifest.IncludesSecrets)
            {
                Console.WriteLine("  The archive has no secrets: secret API keys need this server to use the old server's secrets/ folder.");
            }
        }
        else if (result.ConfigFilesInArchive > 0)
        {
            Console.WriteLine($"  The archive also holds {result.ConfigFilesInArchive} config file(s); re-run with --config to restore them.");
            if (manifest.IncludesSecrets)
            {
                Console.WriteLine("  Secret API keys and player sessions only keep working with the old server's secrets (use --config).");
            }
        }

        Console.WriteLine("Start the server again: sbox-ns service start");
        return CliApp.Ok;
    }
}
