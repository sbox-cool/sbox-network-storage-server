using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class ConfigFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sbox-ns-files-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private EffectiveConfig Load() => ConfigLoader.Load(_dir, Path.Combine(_dir, "data"), null, _ => null);

    [Fact]
    public void WriteMissing_never_overwrites_existing_files()
    {
        File.WriteAllText(Path.Combine(_dir, "server.toml"), "[server]\nlisten = \"127.0.0.1:9999\"\n");

        var written = ConfigFiles.WriteMissing(_dir);

        Assert.DoesNotContain(written, p => p.EndsWith("server.toml", StringComparison.Ordinal));
        Assert.Equal("127.0.0.1:9999", Load().GetString("server.listen"));
    }

    [Fact]
    public void SetValue_replaces_only_the_target_line_and_keeps_comments()
    {
        var path = Path.Combine(_dir, "database.toml");
        File.WriteAllText(path, """
            # my notes about the database
            [database]
            # keep this comment
            provider = "sqlite"   # inline note

            [database.postgres]
            host = "db.internal"
            """);

        ConfigFiles.SetValue(_dir, SettingDefinitions.Find("database.provider")!, "postgres");
        ConfigFiles.SetValue(_dir, SettingDefinitions.Find("database.postgres.port")!, 6543L);
        ConfigFiles.SetValue(_dir, SettingDefinitions.Find("database.sqlite.path")!, "x \"quoted\" \\ path.db");

        var text = File.ReadAllText(path);
        Assert.Contains("# my notes about the database", text);
        Assert.Contains("# keep this comment", text);
        Assert.Contains("provider = \"postgres\"   # inline note", text);
        Assert.Contains("host = \"db.internal\"", text);
        Assert.DoesNotContain("provider = \"sqlite\"", text);

        var config = Load();
        Assert.True(config.IsValid, string.Join("; ", config.Issues));
        Assert.Equal("postgres", config.GetString("database.provider"));
        Assert.Equal(6543, config.GetInteger("database.postgres.port"));
        Assert.Equal("x \"quoted\" \\ path.db", config.GetString("database.sqlite.path"));
        Assert.Equal("db.internal", config.GetString("database.postgres.host"));
    }

    [Fact]
    public void SetValue_does_not_touch_same_named_key_in_other_table()
    {
        var path = Path.Combine(_dir, "server.toml");
        File.WriteAllText(path, "[server]\nlisten = \"0.0.0.0:8080\"\n\n[tls]\nhttps_listen = \"0.0.0.0:443\"\n");

        ConfigFiles.SetValue(_dir, SettingDefinitions.Find("tls.https_listen")!, "0.0.0.0:8443");

        var config = Load();
        Assert.Equal("0.0.0.0:8080", config.GetString("server.listen"));
        Assert.Equal("0.0.0.0:8443", config.GetString("tls.https_listen"));
    }

    [Fact]
    public void SetValue_preserves_comment_after_multiline_string_and_hash_inside_string()
    {
        var path = Path.Combine(_dir, "database.toml");
        File.WriteAllText(path, "[database.sqlite]\npath = \"\"\"old\n# inside the string\npath.db\"\"\" # keep this note\n");

        ConfigFiles.SetValue(_dir, SettingDefinitions.Find("database.sqlite.path")!, "new#path.db");

        Assert.Equal("[database.sqlite]\npath = \"new#path.db\" # keep this note\n", File.ReadAllText(path));
        Assert.Equal("new#path.db", Load().GetString("database.sqlite.path"));
    }
}
