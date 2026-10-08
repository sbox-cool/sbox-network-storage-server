namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// systemd units for named instances and unattended updates. Reference copies live in
/// <c>install/</c> (<c>sbox-ns@.service</c>, <c>sbox-ns-update.service</c>, <c>sbox-ns-update.timer</c>).
/// </summary>
public static class SystemdUnits
{
    public const string InstanceTemplatePath = "/etc/systemd/system/sbox-ns@.service";
    public const string UpdateServicePath = "/etc/systemd/system/sbox-ns-update.service";
    public const string UpdateTimerPath = "/etc/systemd/system/sbox-ns-update.timer";
    public const string UpdateTimer = "sbox-ns-update.timer";

    public static string InstanceTemplate(string binary, string user) => $"""
        # Installed by `sbox-ns service install --instance <name>`. One unit per instance: sbox-ns@<name>.
        # Config in /etc/sbox-ns/<name>, data in /var/lib/sbox-ns/<name>.
        [Unit]
        Description=sbox Network Storage Server (%i)
        Documentation=https://github.com/sbox-cool/sbox-network-storage-server
        Wants=network-online.target
        After=network-online.target postgresql.service

        [Service]
        Type=simple
        User={user}
        ExecStart={binary} start --config-dir /etc/sbox-ns/%i --data-dir /var/lib/sbox-ns/%i
        WorkingDirectory=/var/lib/sbox-ns/%i
        Restart=on-failure
        RestartSec=5
        TimeoutStopSec=30
        Environment=DOTNET_BUNDLE_EXTRACT_BASE_DIR=/var/lib/sbox-ns/%i/.net
        Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
        NoNewPrivileges=true
        ProtectSystem=strict
        ProtectHome=true
        PrivateTmp=true
        PrivateDevices=true
        ProtectKernelTunables=true
        ProtectKernelModules=true
        ProtectControlGroups=true
        RestrictSUIDSGID=true
        LockPersonality=true
        RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX
        ReadWritePaths=/var/lib/sbox-ns/%i /etc/sbox-ns/%i
        UMask=0027

        [Install]
        WantedBy=multi-user.target

        """;

    /// <param name="arguments">Instance selection, e.g. <c>--all-instances</c>.</param>
    public static string UpdateService(string binary, string arguments) => $"""
        # Installed by `sbox-ns service install --auto-update`. Runs as root because it replaces
        # the binary and restarts the sbox-ns units. It installs nothing unless every instance
        # sets updates.auto_install = true; see docs/self-hosting.md "Automatic updates".
        [Unit]
        Description=sbox Network Storage Server unattended update
        Documentation=https://github.com/sbox-cool/sbox-network-storage-server/blob/main/docs/self-hosting.md
        Wants=network-online.target
        After=network-online.target

        [Service]
        Type=oneshot
        ExecStart={binary} update --auto {arguments}
        TimeoutStartSec=30min
        Nice=10

        """;

    public const string UpdateTimerUnit = """
        # Installed by `sbox-ns service install --auto-update`.
        [Unit]
        Description=Run sbox-ns unattended updates every 15 minutes

        [Timer]
        OnCalendar=*:0/15
        RandomizedDelaySec=300
        Persistent=false

        [Install]
        WantedBy=timers.target

        """;
}
