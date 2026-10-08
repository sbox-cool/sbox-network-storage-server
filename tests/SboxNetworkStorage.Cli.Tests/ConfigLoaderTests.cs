using SboxNetworkStorage.Server.Configuration;

namespace SboxNetworkStorage.Cli.Tests;

public sealed class ConfigLoaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sbox-ns-config-").FullName;
    private string ConfigDir => Path.Combine(_root, "config");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteFile(string relativePath, string content)
    {
        var path = Path.Combine(ConfigDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private EffectiveConfig Load(IReadOnlyDictionary<string, string>? flags = null, Dictionary<string, string>? env = null)
        => ConfigLoader.Load(ConfigDir, Path.Combine(_root, "data"), flags, name => env?.GetValueOrDefault(name));

    [Fact]
    public void Missing_folder_yields_valid_defaults()
    {
        var config = Load();

        Assert.True(config.IsValid);
        Assert.Empty(config.LoadedFiles);
        Assert.Equal("sqlite", config.GetString("database.provider"));
        Assert.Equal(SettingSource.Default, config.Values["server.listen"].Source);
    }

    [Fact]
    public void Precedence_is_defaults_then_files_then_confd_then_env_then_flags()
    {
        WriteFile("server.toml", "[server]\nlisten = \"0.0.0.0:1001\"\n[logging]\nlevel = \"Debug\"\n");
        WriteFile("database.toml", "[database]\nprovider = \"sqlite\"\n[database.postgres]\nport = 6000\n");
        WriteFile("conf.d/10-a.toml", "[server]\nlisten = \"0.0.0.0:1002\"\n");
        WriteFile("conf.d/20-b.toml", "[server]\nlisten = \"0.0.0.0:1003\"\n[database]\nprovider = \"postgres\"\n");

        var fromConfD = Load();
        Assert.Equal("0.0.0.0:1003", fromConfD.GetString("server.listen"));
        Assert.Equal(SettingSource.ConfD, fromConfD.Values["server.listen"].Source);
        Assert.Equal("postgres", fromConfD.GetString("database.provider"));
        Assert.Equal("Debug", fromConfD.GetString("logging.level"));
        Assert.Equal(6000, fromConfD.GetInteger("database.postgres.port"));

        var fromEnv = Load(env: new() { ["NS_SERVER__LISTEN"] = "0.0.0.0:1004", ["NS_DATABASE__POSTGRES__PORT"] = "7000" });
        Assert.Equal("0.0.0.0:1004", fromEnv.GetString("server.listen"));
        Assert.Equal(7000, fromEnv.GetInteger("database.postgres.port"));

        var fromFlag = Load(flags: new Dictionary<string, string> { ["server.listen"] = "127.0.0.1:1005" },
            env: new() { ["NS_SERVER__LISTEN"] = "0.0.0.0:1004" });
        Assert.Equal("127.0.0.1:1005", fromFlag.GetString("server.listen"));
        Assert.Equal(SettingSource.Flag, fromFlag.Values["server.listen"].Source);
    }

    [Fact]
    public void Unknown_key_reports_file_line_and_suggestion()
    {
        WriteFile("server.toml", "# comment\n[server]\nlisen = \"0.0.0.0:8080\"\n");

        var config = Load();

        var issue = Assert.Single(config.Issues);
        Assert.Equal("server.toml", issue.File);
        Assert.Equal(3, issue.Line);
        Assert.Contains("did you mean 'server.listen'", issue.Message);
    }

    [Fact]
    public void Key_in_wrong_main_file_is_rejected_but_allowed_in_confd()
    {
        WriteFile("server.toml", "[database]\nprovider = \"postgres\"\n");
        Assert.Contains(Load().Issues, i => i.Message.Contains("belongs in database.toml"));

        File.Delete(Path.Combine(ConfigDir, "server.toml"));
        WriteFile("conf.d/override.toml", "[database]\nprovider = \"postgres\"\n");
        var config = Load();
        Assert.True(config.IsValid);
        Assert.Equal("postgres", config.GetString("database.provider"));
    }

    [Theory]
    [InlineData("[database.postgres]\nport = \"5432\"\n", "must be a whole number")]
    [InlineData("[database]\nprovider = \"mysql\"\n", "must be one of \"sqlite\", \"postgres\"")]
    [InlineData("[database.postgres]\nport = 0\n", "must be greater than 0")]
    [InlineData("[database]\nprovider = \n", "")]
    public void Invalid_values_are_reported(string databaseToml, string expectedFragment)
    {
        WriteFile("database.toml", databaseToml);

        var config = Load();

        Assert.False(config.IsValid);
        Assert.Contains(config.Issues, i => i.File == "database.toml" && i.Line > 0 && i.Message.Contains(expectedFragment));
    }

    [Fact]
    public void Choices_are_case_insensitive_and_normalized()
    {
        WriteFile("database.toml", "[database]\nprovider = \"PostGres\"\n");

        Assert.Equal("postgres", Load().GetString("database.provider"));
    }

    [Theory]
    [InlineData("certificate", "tls.mode = \"certificate\" requires")]
    [InlineData("acme", "tls.mode = \"acme\" requires tls.acme_domain")]
    public void Tls_modes_require_their_settings(string mode, string expected)
    {
        WriteFile("server.toml", $"[tls]\nmode = \"{mode}\"\n");

        Assert.Contains(Load().Issues, i => i.Message.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.0.0.0", false)]
    [InlineData("example.com:8080", false)]
    [InlineData("0.0.0.0:70000", false)]
    [InlineData("[::]:8080", true)]
    [InlineData("localhost:8080", true)]
    public void Listen_addresses_are_validated(string value, bool valid)
    {
        var config = Load(flags: new Dictionary<string, string> { ["server.listen"] = value });

        Assert.Equal(valid, config.IsValid);
    }

    [Fact]
    public void Invalid_environment_value_is_reported_with_variable_name()
    {
        var config = Load(env: new() { ["NS_UPDATES__CHECK"] = "sometimes" });

        var issue = Assert.Single(config.Issues);
        Assert.Equal("NS_UPDATES__CHECK", issue.File);
    }

    [Fact]
    public void Relative_paths_resolve_against_their_base_directories()
    {
        WriteFile("database.toml", "[database.sqlite]\npath = \"nested/ns.db\"\n");

        var config = Load();

        Assert.Equal(Path.Combine(_root, "data", "nested", "ns.db"), config.GetPath("database.sqlite.path", config.DataDirectory));
        Assert.Equal(Path.Combine(ConfigDir, "secrets", "auth_session_secret"), config.GetPath("auth.session_secret_file", config.ConfigDirectory));
    }
}
