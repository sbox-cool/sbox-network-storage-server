using System.Runtime.InteropServices;

namespace SboxNetworkStorage.Server.Configuration;

/// <summary>A Unix account the runtime state belongs to.</summary>
public readonly record struct UnixOwner(uint User, uint Group);

/// <summary>The process-level Unix calls <see cref="RuntimeIdentity"/> needs; tests replace them.</summary>
public interface IIdentityHost
{
    bool IsLinux { get; }

    uint EffectiveUser { get; }

    UnixOwner EffectiveOwner { get; }

    /// <summary>Owner of an existing path, or null when it does not exist.</summary>
    UnixOwner? OwnerOf(string path);

    /// <summary>The account named <paramref name="name"/>, or null when it does not exist.</summary>
    UnixOwner? Lookup(string name);

    void Assume(UnixOwner owner);

    void Restore(UnixOwner original);
}

/// <summary>
/// Files the server and CLI write at runtime belong to the service account. A root-run command (<c>setup</c>,
/// <c>tunnel enable</c>, <c>service install</c>) therefore does its runtime-state file work with the effective
/// user and group of the state folder's owner (else the <c>sbox-ns</c> account, else itself) and never chowns
/// inside a folder the service account controls.
/// </summary>
public static class RuntimeIdentity
{
    public const string ServiceAccount = "sbox-ns";

    public static IIdentityHost Host { get; } = new LinuxIdentityHost();

    /// <summary>The account runtime files should belong to, or null when this process already is that account (or cannot tell).</summary>
    public static UnixOwner? TargetFor(EffectiveConfig config, IIdentityHost host)
    {
        if (!host.IsLinux || host.EffectiveUser != 0)
        {
            return null;
        }

        var target = host.OwnerOf(config.RuntimeDirectory) ?? host.Lookup(ServiceAccount);
        return target is { User: not 0 } found ? found : null;
    }

    /// <summary>Runs the file work of the returned scope as the runtime owner; dispose to return to the original identity.</summary>
    public static IDisposable Enter(EffectiveConfig config) => Enter(config, Host);

    public static IDisposable Enter(EffectiveConfig config, IIdentityHost host)
        => TargetFor(config, host) is { } target ? new Scope(host, host.EffectiveOwner, target) : NoScope.Instance;

    private sealed class NoScope : IDisposable
    {
        public static readonly NoScope Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly IIdentityHost _host;
        private readonly UnixOwner _original;

        public Scope(IIdentityHost host, UnixOwner original, UnixOwner target)
        {
            _host = host;
            _original = original;
            host.Assume(target);
        }

        public void Dispose() => _host.Restore(_original);
    }

    private sealed class LinuxIdentityHost : IIdentityHost
    {
        public bool IsLinux => OperatingSystem.IsLinux();

        public uint EffectiveUser => IsLinux ? GetEffectiveUser() : uint.MaxValue;

        public UnixOwner EffectiveOwner => new(GetEffectiveUser(), GetEffectiveGroup());

        public UnixOwner? OwnerOf(string path) => UnixFiles.OwnerOf(path);

        public UnixOwner? Lookup(string name) => UnixFiles.Lookup(name);

        public void Assume(UnixOwner owner)
        {
            // Group first: once the effective user is unprivileged the group can no longer change.
            Check(SetEffectiveGroup(owner.Group), "setegid");
            Check(SetEffectiveUser(owner.User), "seteuid");
        }

        public void Restore(UnixOwner original)
        {
            Check(SetEffectiveUser(original.User), "seteuid");
            Check(SetEffectiveGroup(original.Group), "setegid");
        }

        private static void Check(int result, string call)
        {
            if (result != 0)
            {
                throw new InvalidOperationException($"cannot switch to the runtime owner ({call} failed); run the command as the service user instead");
            }
        }

        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint GetEffectiveUser();

        [DllImport("libc", EntryPoint = "getegid")]
        private static extern uint GetEffectiveGroup();

        [DllImport("libc", EntryPoint = "seteuid")]
        private static extern int SetEffectiveUser(uint user);

        [DllImport("libc", EntryPoint = "setegid")]
        private static extern int SetEffectiveGroup(uint group);
    }
}
