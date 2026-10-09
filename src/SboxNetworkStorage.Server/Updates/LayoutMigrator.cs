// Linux-only file work: callers go through ILayoutSystem.IsLinux / OperatingSystem.IsLinux before any of it runs.
#pragma warning disable CA1416
using System.Text.Json;
using SboxNetworkStorage.Server.Cli;
using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Server.Updates;

/// <summary>The account an instance's unit runs as.</summary>
public sealed record LayoutAccount(string User, UnixOwner Owner);

/// <summary>Everything <see cref="LayoutMigrator"/> asks of the host; tests replace it so no root or systemd is needed.</summary>
public interface ILayoutSystem
{
    bool IsLinux { get; }

    bool IsRoot { get; }

    /// <summary>The account <paramref name="instance"/>'s unit runs as; throws when it cannot be determined.</summary>
    LayoutAccount ServiceAccount(ServerInstance instance);

    /// <summary>Every instance registered on the host, selected for this run or not; they share the instance unit template.</summary>
    IReadOnlyList<ServerInstance> HostInstances();

    UnixOwner? OwnerOf(string path);

    void Chown(string path, UnixOwner owner);

    /// <summary>The installed unit file for <paramref name="instance"/> (the default unit, or the shared instance template), or null when none is installed.</summary>
    string UnitPathFor(ServerInstance instance);

    string? ReadUnit(string path);

    void WriteUnit(string path, string text);

    Task ReloadUnitsAsync();
}

public enum LayoutOutcome
{
    Migrated,
    Reverted,

    /// <summary>Migrate: every instance already has the marker. Revert: none has it.</summary>
    NothingToDo
}

/// <summary>A migration or revert step failed; the host was put back as far as possible and <see cref="RecoverySteps"/> finishes the rest by hand.</summary>
public sealed class LayoutMigrationException(string message, IReadOnlyList<string> recoverySteps, Exception? inner = null) : Exception(message, inner)
{
    public IReadOnlyList<string> RecoverySteps { get; } = recoverySteps;
}

/// <summary>
/// <c>sbox-ns layout migrate [--revert]</c>. Moves the runtime files of every instance between the config
/// folder and its state folder, fixes ownership and modes, moves the legacy updater state, re-renders the
/// units and writes (or removes) the <see cref="StateLayout.MarkerFile"/> last. Every step is safe to
/// re-run; a failing step undoes the journal and leaves no marker.
/// </summary>
public sealed class LayoutMigrator
{
    private const UnixFileMode OperatorFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
    private const UnixFileMode OperatorFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
    private const UnixFileMode StateFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode StateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode StateExecutable = StateFolder;

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly ILayoutSystem _system;
    private readonly IUpdateHost _host;
    private readonly UpdateState _updateState;
    private readonly string _binary;
    private readonly Action<string> _log;
    private readonly FolderTrust _trust;

    /// <param name="binary">The installed executable; its <c>.update-lock</c> serializes the migration with updates and units are rendered with it.</param>
    public LayoutMigrator(ILayoutSystem system, IUpdateHost host, UpdateState updateState, string binary, Action<string> log, FolderTrust? trust = null)
    {
        _system = system;
        _host = host;
        _updateState = updateState;
        _binary = binary;
        _log = log;
        _trust = trust ?? FolderTrust.Host;
    }

    /// <summary>When false the caller stops and starts the units (the rollback path); the migrator then only moves files.</summary>
    public bool ManageUnits { get; init; } = true;

    /// <summary>True when the caller already holds the update lock (the rollback path).</summary>
    public bool UpdateLockHeld { get; init; }

    public Task<LayoutOutcome> MigrateAsync(IReadOnlyList<ServerInstance> instances, CancellationToken ct) => RunAsync(instances, revert: false, ct);

    public Task<LayoutOutcome> RevertAsync(IReadOnlyList<ServerInstance> instances, CancellationToken ct) => RunAsync(instances, revert: true, ct);

    private async Task<LayoutOutcome> RunAsync(IReadOnlyList<ServerInstance> all, bool revert, CancellationToken ct)
    {
        if (!_system.IsLinux)
        {
            throw new CliException("layout migrate runs on Linux only; other platforms already keep runtime state under the data folder", CliApp.Usage);
        }

        if (!_system.IsRoot)
        {
            throw new CliException("layout migrate must run as root (try: sudo sbox-ns layout migrate)");
        }

        var pending = all.Where(i => StateLayout.HasMarker(i.Config.DataDirectory) == revert).ToList();
        if (pending.Count == 0)
        {
            return LayoutOutcome.NothingToDo;
        }

        using var updateLock = UpdateLockHeld ? null : UpdateCommands.AcquireUpdateLock(_binary);
        foreach (var instance in pending)
        {
            // Root changes ownership inside the config folder, so the folder holding it must not be one the service account controls.
            _trust.Require(Path.GetDirectoryName(Path.GetFullPath(instance.Config.ConfigDirectory))!, "config parent folder");
        }

        var run = new Run(this, all, pending, revert);
        await run.ExecuteAsync(ct);
        return revert ? LayoutOutcome.Reverted : LayoutOutcome.Migrated;
    }

    private sealed class Run(LayoutMigrator migrator, IReadOnlyList<ServerInstance> all, IReadOnlyList<ServerInstance> pending, bool revert)
    {
        private readonly ILayoutSystem _system = migrator._system;
        private readonly List<Action> _undo = [];
        private readonly List<string> _manual = [];
        private readonly HashSet<string> _configRoots = all.Select(i => Path.GetFullPath(i.Config.ConfigDirectory)).ToHashSet(StringComparer.Ordinal);
        private readonly List<ServerInstance> _active = [];
        private readonly Action<string> _log = migrator._log;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            try
            {
                foreach (var instance in pending)
                {
                    if (migrator.ManageUnits && await migrator._host.IsActiveAsync(instance.Unit, ct))
                    {
                        _active.Add(instance);
                    }
                }

                foreach (var instance in _active)
                {
                    _log($"Stopping {instance.Unit}");
                    Require(await migrator._host.StopAsync(instance.Unit, ct), $"stop {instance.Unit}");
                }

                foreach (var instance in pending)
                {
                    _log($"{instance.Name}: {(revert ? "moving runtime files back into" : "moving runtime files out of")} {instance.Config.ConfigDirectory}");
                    MoveFiles(instance);
                }

                foreach (var instance in pending)
                {
                    SetOwnership(instance);
                }

                if (!revert)
                {
                    foreach (var instance in pending)
                    {
                        MoveUpdaterState(instance);
                    }
                }

                await RenderUnitsAsync(ct);
                if (revert)
                {
                    foreach (var instance in pending)
                    {
                        MoveTelemetrySetting(instance);
                    }
                }

                foreach (var instance in _active)
                {
                    _log($"Starting {instance.Unit}");
                    Require(await migrator._host.StartAsync(instance.Unit, ct), $"start {instance.Unit}");
                }

                foreach (var instance in pending)
                {
                    FinishMarker(instance);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw await RecoverAsync(ex);
            }

            FinishUpdaterState();
        }

        // ---- moves -------------------------------------------------------------------------------------------

        private void MoveFiles(ServerInstance instance)
        {
            foreach (var file in StateLayout.RuntimeFiles(instance.Config))
            {
                var (from, to) = revert ? (file.StatePath, file.LegacyPath) : (file.LegacyPath, file.StatePath);
                if (!StateLayout.Exists(from) && new FileInfo(from).LinkTarget is null)
                {
                    continue;
                }

                MoveEntry(from, to);
            }

            if (!revert)
            {
                EnsureStateFolder(instance);
            }
        }

        private void EnsureStateFolder(ServerInstance instance)
        {
            var state = instance.Config.StateDirectory;
            RejectSymbolicLink(state);
            Directory.CreateDirectory(state, StateFolder);
        }

        private void MoveEntry(string from, string to)
        {
            if (new FileInfo(from).LinkTarget is not null)
            {
                throw new InvalidOperationException($"{from} is a symbolic link; refusing to move it");
            }

            if (Directory.Exists(from))
            {
                RejectSymbolicLink(to);
                Directory.CreateDirectory(to, StateFolder);
                foreach (var child in Directory.EnumerateFileSystemEntries(from).ToList())
                {
                    MoveEntry(child, Path.Combine(to, Path.GetFileName(child)));
                }

                if (!Directory.EnumerateFileSystemEntries(from).Any())
                {
                    Directory.Delete(from);
                }

                return;
            }

            var parent = Path.GetDirectoryName(to)!;
            RejectSymbolicLink(parent);
            Directory.CreateDirectory(parent, StateFolder);
            if (File.Exists(to) || new FileInfo(to).LinkTarget is not null)
            {
                if (new FileInfo(to).LinkTarget is null && File.ReadAllBytes(from).AsSpan().SequenceEqual(File.ReadAllBytes(to)))
                {
                    File.Delete(from);
                    return;
                }

                throw new IOException($"{to} already exists with different content than {from}; keep the one that is current and remove the other");
            }

            try
            {
                File.Move(from, to);
            }
            catch (IOException) when (File.Exists(from) && !File.Exists(to))
            {
                // Across file systems a rename is impossible: copy to a temporary name in the target folder, rename, then delete the source.
                var temporary = to + ".migrating-" + Guid.NewGuid().ToString("N");
                File.Copy(from, temporary, overwrite: false);
                File.Move(temporary, to);
                File.Delete(from);
            }

            _undo.Add(() => UndoMove(from, to));
            _manual.Add($"mv -n -- {Quote(to)} {Quote(from)}");
        }

        private static void UndoMove(string from, string to)
        {
            if (!File.Exists(to))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(from)!);
            File.Move(to, from);
        }

        private static void RejectSymbolicLink(string path)
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new InvalidOperationException($"{path} is a symbolic link; refusing to use it");
            }
        }

        // ---- ownership and modes -----------------------------------------------------------------------------

        private void SetOwnership(ServerInstance instance)
        {
            var account = _system.ServiceAccount(instance);
            var config = instance.Config;
            if (revert)
            {
                // The legacy layout: the service account owns its config folder, as v0.4.0 installed it.
                var restored = StateLayout.RuntimeFiles(config).Select(f => f.LegacyPath).ToList();
                ApplyTree(config.ConfigDirectory, account.Owner, OperatorFolder, OperatorFile, _configRoots,
                    ownerOnly: path => restored.Any(r => string.Equals(r, path, StringComparison.Ordinal) || StateLayout.IsInside(r, path)));
                return;
            }

            ApplyTree(config.ConfigDirectory, new UnixOwner(0, account.Owner.Group), OperatorFolder, OperatorFile, _configRoots, ownerOnly: _ => false);
            ApplyTree(config.StateDirectory, account.Owner, StateFolder, StateFile, [], ownerOnly: _ => true);
        }

        /// <param name="ownerOnly">Files that get owner-only access (keeping the owner's execute bit).</param>
        private void ApplyTree(string root, UnixOwner owner, UnixFileMode folderMode, UnixFileMode fileMode, HashSet<string> skipFolders, Func<string, bool> ownerOnly)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            RejectSymbolicLink(root);
            Apply(root, owner, folderMode);
            Walk(root);

            void Walk(string folder)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
                {
                    if (new FileInfo(entry).LinkTarget is not null)
                    {
                        continue;
                    }

                    if (Directory.Exists(entry))
                    {
                        if (skipFolders.Contains(Path.GetFullPath(entry)))
                        {
                            continue;
                        }

                        Apply(entry, owner, folderMode);
                        Walk(entry);
                        continue;
                    }

                    var mode = fileMode;
                    if (ownerOnly(entry))
                    {
                        mode = (File.GetUnixFileMode(entry) & UnixFileMode.UserExecute) != 0 ? StateExecutable : StateFile;
                    }

                    Apply(entry, owner, mode);
                }
            }
        }

        private void Apply(string path, UnixOwner owner, UnixFileMode mode)
        {
            var previousOwner = _system.OwnerOf(path);
            var previousMode = File.GetUnixFileMode(path);
            if (previousOwner != owner)
            {
                _system.Chown(path, owner);
            }

            File.SetUnixFileMode(path, mode);
            _undo.Add(() =>
            {
                if (previousOwner is { } original)
                {
                    _system.Chown(path, original);
                }

                File.SetUnixFileMode(path, previousMode);
            });
            if (previousOwner is { } was)
            {
                _manual.Add($"chown -h {was.User}:{was.Group} -- {Quote(path)} && chmod {Convert.ToString((int)previousMode, 8)} -- {Quote(path)}");
            }
        }

        // ---- updater state -----------------------------------------------------------------------------------

        private readonly List<string> _legacyUpdaterFiles = [];

        private void MoveUpdaterState(ServerInstance instance)
        {
            var legacy = Path.Combine(instance.Config.DataDirectory, "updates", "last-update.json");
            if (File.Exists(legacy))
            {
                if (migrator._updateState.Read(instance.Name) is null)
                {
                    UpdateRecord? record = null;
                    try
                    {
                        record = JsonSerializer.Deserialize<UpdateRecord>(File.ReadAllText(legacy), ReadOptions);
                    }
                    catch (JsonException)
                    {
                        _log($"{instance.Name}: {legacy} is not readable; it was left where it is");
                    }

                    if (record is not null)
                    {
                        // Only the record's own fields are copied; the file itself was writable by the service account.
                        migrator._updateState.Write(instance.Name, new UpdateRecord(record.FromVersion, record.ToVersion, record.BinaryPath,
                            migrator._updateState.PreviousBinaryPath, record.BackupPath, record.UpdatedAt, record.Status, record.Reason, record.Mode));
                        var written = migrator._updateState.RecordPath(instance.Name);
                        _undo.Add(() => File.Delete(written));
                        _manual.Add($"rm -f -- {Quote(written)}");
                        _legacyUpdaterFiles.Add(legacy);
                    }
                }
                else
                {
                    _legacyUpdaterFiles.Add(legacy);
                }
            }

            var previous = migrator._binary + ".previous";
            var target = migrator._updateState.PreviousBinaryPath;
            if (File.Exists(previous) && new FileInfo(previous).LinkTarget is null && !File.Exists(target))
            {
                migrator._updateState.Ensure();
                File.Copy(previous, target);
                _undo.Add(() => File.Delete(target));
                _manual.Add($"rm -f -- {Quote(target)}");
                _legacyUpdaterFiles.Add(previous);
            }
        }

        /// <summary>The legacy copies are removed only after everything else succeeded; a leftover is harmless.</summary>
        private void FinishUpdaterState()
        {
            foreach (var file in _legacyUpdaterFiles.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log($"could not remove the old copy {file} ({ex.Message}); it is no longer used");
                }
            }
        }

        // ---- units and the setting that moved ----------------------------------------------------------------

        private async Task RenderUnitsAsync(CancellationToken ct)
        {
            // The instance template is shared: it keeps the config folder writable while any instance still uses the legacy layout.
            var templateWritesConfig = revert
                || _system.HostInstances().Any(i => !pending.Any(p => p.Name == i.Name) && i.Config.Layout == ConfigLayout.Legacy);
            var changed = false;
            foreach (var instance in pending)
            {
                var path = _system.UnitPathFor(instance);
                var existing = _system.ReadUnit(path);
                if (existing is null)
                {
                    continue;
                }

                var user = _system.ServiceAccount(instance).User;
                var text = instance.Name == ServerInstance.DefaultName
                    ? ServiceCommands.SystemdUnit(migrator._binary, instance.Config, user, writableConfig: revert)
                    : SystemdUnits.InstanceTemplate(migrator._binary, user, writableConfig: templateWritesConfig);
                if (text == existing)
                {
                    continue;
                }

                _system.WriteUnit(path, text);
                changed = true;
                _undo.Add(() => _system.WriteUnit(path, existing));
                _manual.Add($"restore {path} from the copy kept by your configuration management or `systemctl cat`, then run systemctl daemon-reload");
            }

            if (changed)
            {
                await _system.ReloadUnitsAsync();
            }
        }

        /// <summary>Legacy installs keep <c>telemetry.enabled</c> in server.toml; carry the overlay's value back before the overlay goes.</summary>
        private void MoveTelemetrySetting(ServerInstance instance)
        {
            var overlay = ManagedOverlay.PathFor(instance.Config, StateLayout.TelemetryOverlayFile);
            if (!File.Exists(overlay))
            {
                return;
            }

            ConfigFiles.SetValue(instance.Config.ConfigDirectory, SettingDefinitions.Find("telemetry.enabled")!, instance.Config.GetBoolean("telemetry.enabled"));
            File.Delete(overlay);
        }

        // ---- marker ------------------------------------------------------------------------------------------

        private void FinishMarker(ServerInstance instance)
        {
            var marker = StateLayout.MarkerPath(instance.Config.DataDirectory);
            if (revert)
            {
                File.Delete(marker);
                return;
            }

            if (File.Exists(marker))
            {
                return;
            }

            // Create-new under a temporary name then rename: never writes through a symbolic link planted in the state folder.
            var temporary = marker + ".tmp-" + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = StateFile }))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(StateLayout.CurrentVersion + "\n");
            }

            _system.Chown(temporary, _system.ServiceAccount(instance).Owner);
            File.Move(temporary, marker, overwrite: true);
            _log($"{instance.Name}: layout marker written ({marker})");
        }

        // ---- failure -----------------------------------------------------------------------------------------

        private async Task<LayoutMigrationException> RecoverAsync(Exception failure)
        {
            _log($"Step failed: {failure.Message}. Undoing the changes made so far.");
            foreach (var instance in _active)
            {
                try
                {
                    await migrator._host.StopAsync(instance.Unit, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log($"could not stop {instance.Unit}: {ex.Message}");
                }
            }

            var remaining = new List<string>();
            var undone = 0;
            for (var i = _undo.Count - 1; i >= 0; i--)
            {
                try
                {
                    _undo[i]();
                    undone++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    remaining.Add($"{ex.Message}");
                }
            }

            // Anything the units wrote while they ran must not mark the layout as migrated.
            if (!revert)
            {
                foreach (var instance in pending)
                {
                    TryDelete(StateLayout.MarkerPath(instance.Config.DataDirectory));
                    PruneEmptyFolders(instance.Config.StateDirectory);
                }
            }

            var restored = remaining.Count == 0;
            if (restored)
            {
                try
                {
                    await _system.ReloadUnitsAsync();
                }
                catch (Exception ex) when (ex is IOException or CliException)
                {
                    _log($"systemctl daemon-reload failed: {ex.Message}");
                }

                foreach (var instance in _active)
                {
                    await migrator._host.StartAsync(instance.Unit, CancellationToken.None);
                }
            }

            var steps = new List<string>();
            if (restored)
            {
                steps.Add("Every change was undone and no layout marker was written; the host is as it was before the command.");
                steps.Add("Fix the problem above, then run the command again.");
            }
            else
            {
                steps.Add($"{remaining.Count} undo step(s) failed ({string.Join("; ", remaining)}). Finish by hand, stopping the units first:");
                foreach (var instance in _active)
                {
                    steps.Add($"systemctl stop {instance.Unit}");
                }

                steps.AddRange(Enumerable.Reverse(_manual));
                steps.Add("systemctl daemon-reload");
                foreach (var instance in _active)
                {
                    steps.Add($"systemctl start {instance.Unit}");
                }

                steps.Add("`sbox-ns doctor` reports the layout as partial until the runtime files are back in one place.");
            }

            return new LayoutMigrationException(failure.Message, steps, failure);
        }

        /// <summary>Removes the folders the undone moves left empty, the state folder itself included.</summary>
        private static void PruneEmptyFolders(string folder)
        {
            if (!Directory.Exists(folder) || new FileInfo(folder).LinkTarget is not null)
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                PruneEmptyFolders(child);
            }

            if (!Directory.EnumerateFileSystemEntries(folder).Any())
            {
                try
                {
                    Directory.Delete(folder);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private static void Require(int exitCode, string step)
        {
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"{step} exited with code {exitCode}");
            }
        }

        private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";
    }

}
