#:property PublishAot=false
// Drives the real Network Storage client library inside s&box through sbox-cli (s&box 26.10.13+):
// the editor (Sync Tool push, play-mode calls) and/or a dedicated server started with -mcp, against
// sbox-ns. Writes evidence.json, summary.md and a draft compat/library-matrix.json `tested` row.
// Usage: dotnet run tools/sbox-cli-e2e/run.cs -- --help
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var o = Options.Parse(args);
if (o is null) return 2;
Directory.CreateDirectory(o.Out);
var ev = new Evidence(o.Out);
var started = new List<Process>();
var exit = 1;
var runStarted = DateTime.UtcNow;
try
{
	exit = await E2E.RunAsync(o, ev, started);
}
catch (Exception error)
{
	ev.Fail("runner", error.Message);
	Console.Error.WriteLine($"sbox-cli e2e: {error.Message}");
}
finally
{
	foreach (var p in started) Proc.Kill(p);
	Files.CopyRecentLogs(Path.Combine(o.Engine, "logs"), Path.Combine(o.Out, "engine-logs"), runStarted);
	ev.Save();
}
return exit;

sealed class Options
{
	public string Root = "";
	public string Engine = "";
	public string Library = "main";
	public string Targets = "editor,dedicated";
	public string Out = "";
	public int Port = 8080;
	public bool Build = true;
	public bool Attach;
	public bool RequireSboxAuth;
	public string? ServerUrl, ProjectId, PublicKey, SecretKey, ServerBinary;
	public List<string> EvidenceRefs = [];
	public int StartupTimeout = 900;
	public bool Editor => Targets.Contains("editor");
	public bool Dedicated => Targets.Contains("dedicated");

	const string Help = """
	sbox-cli e2e: the real Network Storage library in s&box, driven through sbox-cli.

	  --engine <game dir>      s&box game folder with bin/managed/sbox-cli (default $SBOX_GAME, then the
	                           Steam install on Windows)
	  --library <source>       main (default), latest (the current sbox.game revision's GitHub release,
	                           else main), git:<ref>, r<revision> (that revision's release tag), a local
	                           checkout path, or package (editor installs the published sbox.game
	                           package through Library Manager)
	  --targets <list>         editor,dedicated (default). editor needs Windows (sbox-cli launches) unless
	                           --attach; dedicated works wherever sbox-server runs
	  --attach                 use an editor you already opened on the fixture printed by the runner
	  --server-url <url>       test an existing server instead of building this checkout; also pass
	                           --project, --public-key and --secret-key
	  --server-binary <path>   run this sbox-ns executable (for example an unpacked release's sbox-ns.exe)
	                           instead of building this checkout
	  --port <80|443|8080|8443> port for the managed server (default 8080; s&box game code only reaches
	                           loopback on these ports)
	  --require-sbox-auth      create the managed project with s&box auth on (headless runs have no token)
	  --no-build               reuse the existing Release build of this checkout
	  --evidence <ref>         issue/PR/URL for the draft tested row (repeatable)
	  --startup-timeout <s>    editor/dedicated startup budget (default 900)
	  --out <dir>              output folder (default artifacts/sbox-cli-e2e/<timestamp>)
	""";

	public static Options? Parse(string[] args)
	{
		var o = new Options { Root = FindRoot() };
		for (var i = 0; i < args.Length; i++)
		{
			string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
			switch (args[i])
			{
				case "--engine": o.Engine = Next(); break;
				case "--library": o.Library = Next(); break;
				case "--targets": o.Targets = Next().ToLowerInvariant(); break;
				case "--attach": o.Attach = true; break;
				case "--server-url": o.ServerUrl = Next().TrimEnd('/'); break;
				case "--server-binary": o.ServerBinary = Path.GetFullPath(Next()); break;
				case "--project": o.ProjectId = Next(); break;
				case "--public-key": o.PublicKey = Next(); break;
				case "--secret-key": o.SecretKey = Next(); break;
				case "--port": o.Port = int.Parse(Next()); break;
				case "--require-sbox-auth": o.RequireSboxAuth = true; break;
				case "--no-build": o.Build = false; break;
				case "--evidence": o.EvidenceRefs.Add(Next()); break;
				case "--startup-timeout": o.StartupTimeout = int.Parse(Next()); break;
				case "--out": o.Out = Path.GetFullPath(Next()); break;
				default: Console.Error.WriteLine(Help); return null;
			}
		}
		if (o.Port is not (80 or 443 or 8080 or 8443)) { Console.Error.WriteLine("--port must be 80, 443, 8080 or 8443 (s&box loopback allowlist)"); return null; }
		if (!o.Editor && !o.Dedicated) { Console.Error.WriteLine("--targets needs editor and/or dedicated"); return null; }
		if (o.ServerUrl is not null && (o.ProjectId is null || o.PublicKey is null || o.SecretKey is null)) { Console.Error.WriteLine("--server-url needs --project, --public-key and --secret-key"); return null; }
		if (o.ServerBinary is not null && o.ServerUrl is not null) { Console.Error.WriteLine("--server-binary and --server-url are exclusive"); return null; }
		if (o.ServerBinary is not null && !File.Exists(o.ServerBinary)) { Console.Error.WriteLine($"--server-binary: {o.ServerBinary} does not exist"); return null; }
		if (o.Library == "package" && !o.Editor) { Console.Error.WriteLine("--library package installs through the editor; add the editor target"); return null; }
		if (string.IsNullOrEmpty(o.Engine)) o.Engine = Environment.GetEnvironmentVariable("SBOX_GAME") ?? DefaultEngine();
		o.Engine = Path.GetFullPath(o.Engine);
		if (string.IsNullOrEmpty(o.Out)) o.Out = Path.Combine(o.Root, "artifacts", "sbox-cli-e2e", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
		return o;
	}

	static string DefaultEngine() => OperatingSystem.IsWindows()
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "sbox")
		: "";

	static string FindRoot()
	{
		for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
			if (File.Exists(Path.Combine(d.FullName, "SboxNetworkStorage.sln"))) return d.FullName;
		throw new InvalidOperationException("run from inside the sbox-network-storage-server checkout");
	}
}

static class E2E
{
	const string LibraryRepo = "https://github.com/sbox-cool/sbox-network-storage.git";
	const string PackageIdent = "sboxcool.network-storage";

	public static async Task<int> RunAsync(Options o, Evidence ev, List<Process> started)
	{
		var exe = OperatingSystem.IsWindows() ? ".exe" : "";
		var cliPath = Path.Combine(o.Engine, "bin", "managed", "sbox-cli" + exe);
		var cliDll = Path.Combine(o.Engine, "bin", "managed", "sbox-cli.dll");
		if (!File.Exists(cliPath) && !File.Exists(cliDll))
			throw new InvalidOperationException($"no sbox-cli in {o.Engine}/bin/managed (s&box 26.10.13+; pass --engine <game dir>)");
		var work = Path.Combine(o.Out, "work");
		Directory.CreateDirectory(work);

		// 1. Server.
		var server = o.ServerUrl is null ? await StartServerAsync(o, work, ev, started) : new Server(o.ServerUrl, o.ProjectId!, o.PublicKey!, o.SecretKey!, null, null);
		var info = await Http.GetJsonAsync($"{server.Url}/v3/server-info");
		ev.Identity["server"] = new JsonObject
		{
			["version"] = info?["version"]?.GetValue<string>(), ["kind"] = info?["kind"]?.GetValue<string>(),
			["database"] = info?["database"]?.GetValue<string>(), ["url"] = server.Url, ["commit"] = server.Commit,
		};
		Console.WriteLine($"server {info?["version"]} ({info?["kind"]}, {info?["database"]}) at {server.Url}");

		// 2. Fixture project with the library.
		var fixture = Path.Combine(work, "compat_probe");
		Files.CopyDirectory(Path.Combine(o.Root, "tools", "sbox-cli-e2e", "fixture"), fixture);
		Fixture.WriteConfig(fixture, server);
		var lib = o.Library == "package" ? null : await InstallLibraryAsync(o, work, fixture, ev);
		var cli = new SboxCli(cliPath, cliDll, fixture, Path.Combine(o.Out, "sbox-cli.log"));
		Console.WriteLine($"fixture: {Path.Combine(fixture, "compat_probe.sbproj")}");

		// 3. Editor.
		if (o.Editor) await EditorAsync(o, ev, cli, fixture, server, started, exe);
		if (o.Library == "package") lib = Library.FromInstalled(Path.Combine(fixture, "Libraries", PackageIdent), "library-manager");
		if (lib is not null) ev.Identity["library"] = lib.ToJson();

		// 4. Dedicated server (pushes the fixture definitions itself when the editor did not).
		if (o.Dedicated)
		{
			if (!o.Editor) await Fixture.PushSourcesAsync(fixture, server, ev);
			await DedicatedAsync(o, ev, cli, fixture, server, started, exe);
		}

		// 5. What the server saw.
		if (server.Cli is not null)
		{
			var requests = await server.Cli.RunAsync(["dev", "logs_requests"], stdin: new JsonObject { ["projectId"] = server.ProjectId, ["limit"] = 200 }.ToJsonString());
			File.WriteAllText(Path.Combine(o.Out, "server-requests.json"), requests.Stdout);
			var version = lib?.Version;
			// Client versions are batched about once a minute (docs/game-client.md), so poll.
			Proc.Result clients = new(1, "", "");
			var seen = await WaitAsync(150, async () =>
			{
				clients = await server.Cli.RunAsync(["project", "clients", server.ProjectId, "--days", "1", "--json"]);
				return version is null || clients.Stdout.Contains($"\"{version}\"") || clients.Code != 0;
			});
			File.WriteAllText(Path.Combine(o.Out, "server-clients.json"), clients.Stdout + clients.Stderr);
			if (version is not null && clients.Code != 0 && o.ServerBinary is not null)
			{
				// sbox-ns 0.5.4 and older crash on `project clients` outside the web host (fixed in #97), so a prebuilt
				// older server (--server-binary) cannot report what it recorded. That is a server CLI defect, not a
				// library/server compatibility result: record it without failing the run.
				ev.Observe("version.header", $"not checked: `sbox-ns project clients` exited {clients.Code} on this server build");
			}
			else if (version is not null)
			{
				ev.Check("version.header", "G", seen, $"server recorded x-ns-library-version {version} (project clients)", required: true);
			}
		}

		var engine = ev.Identity["engine"]?["version"]?.GetValue<string>();
		// Source builds report 0000000, which identifies no build. Leave the row's engine out (optional for
		// ci-only rows; a pass row without it fails the schema) and name the sbox-public commit in its notes.
		if (engine is not null && engine.Trim('0').Length == 0)
			engine = null;
		ev.WriteRow(o, engine, lib);
		var pass = ev.Passed;
		Console.WriteLine(pass ? "SBOX-CLI E2E PASS" : "SBOX-CLI E2E FAIL");
		Console.WriteLine($"evidence: {o.Out}");
		return pass ? 0 : 1;
	}

	static async Task<Server> StartServerAsync(Options o, string work, Evidence ev, List<Process> started)
	{
		var serverProject = Path.Combine(o.Root, "src", "SboxNetworkStorage.Server");
		string[] command;
		if (o.ServerBinary is not null)
		{
			command = [o.ServerBinary];
		}
		else
		{
			if (o.Build)
			{
				var build = await Proc.RunAsync("dotnet", ["build", serverProject, "-c", "Release", "-v", "q", "-nologo"], o.Root, TimeSpan.FromMinutes(10));
				if (build.Code != 0) throw new InvalidOperationException("server build failed:\n" + build.Stdout + build.Stderr);
			}
			command = ["dotnet", Path.Combine(serverProject, "bin", "Release", "net10.0", "sbox-ns.dll")];
		}
		var ns = new NsCli(command, Path.Combine(work, "ns", "config"), Path.Combine(work, "ns", "data"));
		var setup = await ns.RunAsync(["setup", "--non-interactive", "--database", "sqlite", "--listen", $"127.0.0.1:{o.Port}"]);
		if (setup.Code != 0) throw new InvalidOperationException("sbox-ns setup failed: " + setup.Stderr);
		var quick = await ns.RunAsync(["quickstart", "sbox-cli e2e", "--require-sbox-auth", o.RequireSboxAuth ? "true" : "false", "--json"]);
		var q = JsonNode.Parse(quick.Stdout) ?? throw new InvalidOperationException("quickstart returned no JSON: " + quick.Stderr);
		var log = Path.Combine(o.Out, "sbox-ns.log");
		started.Add(Proc.Start(command[0], [.. command[1..], "start", .. ns.DirArgs], o.Root, log));
		var url = $"http://127.0.0.1:{o.Port}";
		for (var i = 0; i < 120 && !await Http.OkAsync($"{url}/health"); i++) await Task.Delay(500);
		if (!await Http.OkAsync($"{url}/health")) throw new InvalidOperationException($"sbox-ns did not answer /health; see {log}");
		// A prebuilt binary's source commit is not this checkout's; /v3/server-info still reports its version.
		var commit = o.ServerBinary is null
			? (await Proc.RunAsync("git", ["rev-parse", "HEAD"], o.Root, TimeSpan.FromSeconds(10))).Stdout.Trim()
			: null;
		return new Server(url, q["projectId"]!.GetValue<string>(), q["publicKey"]!.GetValue<string>(), q["secretKey"]!.GetValue<string>(), ns, commit);
	}

	static async Task<Library> InstallLibraryAsync(Options o, string work, string fixture, Evidence ev)
	{
		var target = Path.Combine(fixture, "Libraries", PackageIdent);
		string checkout;
		string? releaseTag = null;
		if (Directory.Exists(o.Library))
		{
			checkout = Path.GetFullPath(o.Library);
		}
		else
		{
			var reference = o.Library switch
			{
				"main" => "main",
				"latest" => await LatestReleaseTagAsync() ?? "main",
				_ when o.Library.StartsWith("git:") => o.Library[4..],
				_ => o.Library,
			};
			if (Regex.IsMatch(reference, @"^r\d+$")) releaseTag = reference;
			checkout = Path.Combine(work, "library-src");
			await Git(work, "clone", "-q", LibraryRepo, checkout);
			await Git(checkout, "checkout", "-q", reference);
		}
		// Copy only what git tracks, like a clean `git clone` install (no obj/, no stale build output).
		var tracked = (await Proc.RunAsync("git", ["ls-files", "-z"], checkout, TimeSpan.FromSeconds(30))).Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
		if (tracked.Length == 0) Files.CopyDirectory(checkout, target);
		foreach (var file in tracked)
		{
			var destination = Path.Combine(target, file);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(Path.Combine(checkout, file), destination, overwrite: true);
		}
		var sha = (await Proc.RunAsync("git", ["rev-parse", "HEAD"], checkout, TimeSpan.FromSeconds(10))).Stdout.Trim();
		if (releaseTag is null)
		{
			var tags = (await Proc.RunAsync("git", ["tag", "--points-at", "HEAD"], checkout, TimeSpan.FromSeconds(10))).Stdout;
			releaseTag = tags.Split('\n', StringSplitOptions.TrimEntries).FirstOrDefault(t => Regex.IsMatch(t, @"^r\d+$"));
		}
		var lib = Library.FromInstalled(target, releaseTag is null ? $"git:{sha[..7]}" : $"release:{releaseTag}");
		lib.Commit = sha;
		if (releaseTag is not null) lib.Revision = long.Parse(releaseTag[1..]);
		Console.WriteLine($"library {lib.Version} from {lib.Source} ({sha[..7]})");
		return lib;
	}

	static async Task EditorAsync(Options o, Evidence ev, SboxCli cli, string fixture, Server server, List<Process> started, string exe)
	{
		var project = Path.Combine(fixture, "compat_probe.sbproj");
		if (!o.Attach)
		{
			if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("sbox-cli can only launch the editor on Windows; open the fixture yourself and pass --attach, or use --targets dedicated");
			// The editor itself needs a running, logged-in Steam client (it shows "We need Steam!" otherwise).
			started.Add(Proc.Start(Path.Combine(o.Engine, "sbox-dev" + exe), ["-mcp", "-project", project], o.Engine, Path.Combine(o.Out, "sbox-dev.stdout.log")));
		}
		else
		{
			Console.WriteLine($"attach mode: open {project} in the s&box editor now (waiting up to {o.StartupTimeout}s)");
		}
		if (!await WaitAsync(o.StartupTimeout, async () => (await cli.RunAsync(["--list"], 30)).Stdout.Contains("compat_probe")))
			throw new InvalidOperationException("the editor never showed up in sbox-cli --list (Steam running and logged in? see the editor log)");

		var status = await WaitForIdleAsync(cli, null, editor: true, o.StartupTimeout);
		ev.Identity["engine"] = new JsonObject { ["version"] = J.Str(status.Instance, "version"), ["target"] = "editor" };
		ev.Identity["os"] = RuntimeInformation.OSDescription;

		if (o.Library == "package")
		{
			var install = await cli.CallAsync("install_package", new JsonObject { ["ident"] = PackageIdent }, timeout: 600);
			ev.Step("editor.install_package", install);
			status = await WaitForIdleAsync(cli, null, editor: true, o.StartupTimeout);
		}

		var compiled = J.Bool(status.Editor, "lastCompileSucceeded");
		ev.Check("editor.compile", "A", compiled, compiled ? "library and fixture compile in the editor" : "compile failed: " + status.Editor?["lastCompileErrors"]?.ToJsonString(), required: true);
		File.WriteAllText(Path.Combine(ev.Dir, "compile_status.json"), (await cli.CallAsync("compile_status")).Text);
		var toolset = HasTool(await cli.CallAsync("search_tools", new JsonObject { ["query"] = "ns_sync_push" }), "ns_sync_push");
		ev.Identity["driver"] = toolset ? "network_storage toolset" : "run_csharp";
		if (!compiled) return;

		// B. Sync Tool push of the fixture definitions.
		if (toolset)
		{
			var editorStatus = await cli.CallAsync("ns_editor_status", new JsonObject { ["checkServer"] = true });
			ev.Step("editor.ns_editor_status", editorStatus);
			ev.Check("editor.validate", "A", J.Bool(editorStatus.Json?["server"], "ok"), "Setup credentials validate against the server", required: true);
			var push = await cli.CallAsync("ns_sync_push", new JsonObject { ["target"] = "live", ["verify"] = true }, timeout: 600);
			ev.Step("editor.ns_sync_push", push);
			ev.Check("sync.push", "B", push.Ok && J.Bool(push.Json, "ok"), "Push All: preflight, PUT sync and post-push verification", required: true);
			var autoTest = await cli.CallAsync("ns_auto_test", new JsonObject { ["target"] = "live" }, timeout: 600);
			ev.Step("editor.ns_auto_test", autoTest);
			ev.Check("sync.auto-test", "B", autoTest.Ok && J.Bool(autoTest.Json, "ok"), "Test Live (auto-test) ran; per-endpoint results in evidence.json", required: false);
		}
		else
		{
			var sync = await cli.CallAsync("run_csharp", new JsonObject { ["code"] = Scripts.EditorSync }, timeout: 600);
			ev.Step("editor.sync(run_csharp)", sync);
			ev.Check("editor.validate", "A", J.Str(sync.Json, "validate") == "ok", "Setup credentials validate against the server", required: true);
			ev.Check("sync.push", "B", J.Bool(sync.Json, "pass"), "Push All: preflight and PUT sync (no post-push verification without the toolset)", required: true);
		}

		// C. Play mode with the real library.
		var play = await cli.CallAsync("play_start");
		ev.Step("editor.play_start", play);
		await WaitAsync(120, async () => J.Bool((await cli.CallAsync("instance_status")).Json, "isPlaying"));
		await Task.Delay(3000);
		await RuntimeProbeAsync(ev, cli, null, server, fixture, "editor", toolset);
		File.WriteAllText(Path.Combine(ev.Dir, "editor-console.txt"), (await cli.CallAsync("read_console", new JsonObject { ["filter"] = "NetworkStorage", ["limit"] = 200 })).Text);
		ev.Step("editor.play_stop", await cli.CallAsync("play_stop"));
		if (!o.Attach) await cli.CallAsync("instance_console_command", new JsonObject { ["command"] = "quit" });
	}

	static async Task DedicatedAsync(Options o, Evidence ev, SboxCli cli, string fixture, Server server, List<Process> started, string exe)
	{
		var log = Path.Combine(o.Out, "sbox-server.log");
		var ds = Proc.Start(Path.Combine(o.Engine, "sbox-server" + exe),
			["+game", Path.Combine(fixture, "compat_probe.sbproj"), "-mcp", "-allowlocalhttp", "+network_storage_secret_key", server.SecretKey], o.Engine, log);
		started.Add(ds);
		var pid = ds.Id.ToString();
		if (!await WaitAsync(o.StartupTimeout, async () => !ds.HasExited && (await cli.CallAsync("instance_status", connect: pid, timeout: 20)).Ok))
			throw new InvalidOperationException($"sbox-server never answered sbox-cli (exited: {ds.HasExited}); see {log}");
		// The process answers before `+game` has loaded the project; wait until it reports one.
		var status = await WaitForIdleAsync(cli, pid, editor: false, o.StartupTimeout, requireProject: true);
		if (J.Str(status.Instance, "project") is null && J.Str(status.Instance, "game") is null)
			throw new InvalidOperationException($"sbox-server never loaded the fixture project; see {Path.Combine(o.Out, "engine-logs")}");
		ev.Identity["engine"] ??= new JsonObject { ["version"] = J.Str(status.Instance, "version"), ["commit"] = await EngineCommitAsync(o.Engine), ["target"] = "dedicated" };
		ev.Identity["os"] ??= RuntimeInformation.OSDescription;
		var toolset = HasTool(await cli.CallAsync("search_tools", new JsonObject { ["query"] = "ns_call_endpoint" }, connect: pid), "ns_call_endpoint");
		ev.Identity["dedicatedDriver"] = toolset ? "network_storage toolset" : "run_csharp";
		await RuntimeProbeAsync(ev, cli, pid, server, fixture, "dedicated", toolset);
		File.WriteAllText(Path.Combine(ev.Dir, "dedicated-console.txt"), (await cli.CallAsync("read_console", new JsonObject { ["filter"] = "NetworkStorage", ["limit"] = 200 }, connect: pid)).Text);
	}

	/// <summary>
	/// docs/compatibility.md: a library whose default root is /api is pinned to v3 on servers that do not serve /api.
	/// Only libraries with a non-v3 default and the apiVersion Configure overload are pinned, and only when the server
	/// does not advertise the api-root capability. Every other combination keeps the library's own default.
	/// </summary>
	static async Task<string?> ConfigureApiRootAsync(Evidence ev, Server server, string fixture, string target)
	{
		var lib = Library.FromInstalled(Path.Combine(fixture, "Libraries", PackageIdent), "");
		var info = await Http.GetJsonAsync($"{server.Url}/v3/server-info");
		var advertised = info?["capabilities"] is JsonArray caps && caps.Any(c => c is JsonValue v && v.TryGetValue<string>(out var s) && s == "api-root");
		var pin = lib.ConfigureTakesApiRoot && lib.DefaultApiRoot is { Length: > 0 } d && d != "v3" && !advertised;
		var root = pin ? "v3" : null;
		ev.Observe($"api-root.{target}", $"library default {lib.DefaultApiRoot ?? "unknown"}; apiVersion overload {(lib.ConfigureTakesApiRoot ? "yes" : "no")}; server advertises api-root {(advertised ? "yes" : "no")}; configured root {root ?? "library default"}");
		Console.WriteLine($"api root ({target}): {root ?? "library default"}");
		return root;
	}

	/// <summary>Real library calls in play mode (editor) or on the dedicated server, checked against the fixture definitions.</summary>
	static async Task RuntimeProbeAsync(Evidence ev, SboxCli cli, string? pid, Server server, string fixture, string target, bool toolset)
	{
		var dedicated = target == "dedicated";
		var apiRoot = await ConfigureApiRootAsync(ev, server, fixture, target);
		var configure = toolset && apiRoot is null
			? await cli.CallAsync("ns_configure", new JsonObject { ["projectId"] = server.ProjectId, ["apiKey"] = server.PublicKey, ["baseUrl"] = server.Url }, connect: pid)
			: await cli.CallAsync("run_csharp", new JsonObject { ["code"] = Scripts.Configure(server, apiRoot) }, connect: pid);
		ev.Step($"{target}.configure", configure);
		var allowed = J.Bool(configure.Json, "httpAllowed") || J.Bool(configure.Json, "allowed");
		ev.Check($"{target}.http-allowed", "C", allowed, $"game code may reach {server.Url} (Http.IsAllowed)", required: true);

		async Task<JsonNode?> Call(string slug, JsonObject? input = null)
		{
			var r = toolset
				? await cli.CallAsync("ns_call_endpoint", new JsonObject { ["slug"] = slug, ["input"] = input }, connect: pid, timeout: 120)
				: await cli.CallAsync("run_csharp", new JsonObject { ["code"] = Scripts.CallEndpoint(slug, input) }, connect: pid, timeout: 120);
			ev.Step($"{target}.call.{slug}", r);
			return r.Json;
		}
		static bool Ok(JsonNode? r) => J.Bool(r, "ok");
		static string Code(JsonNode? r) => J.Str(r, "errorCode") ?? "";
		static string Body(JsonNode? r) => r?["body"] is JsonNode b ? (b is JsonValue v && v.TryGetValue<string>(out var s) ? s : b.ToJsonString()) : "";

		var ping = await Call("ping");
		ev.Check($"{target}.endpoint", "C", Ok(ping) && Body(ping).Contains("pong"), "CallEndpoint success (ping)", required: true);
		var echo = await Call("echo", new JsonObject { ["hello"] = "world" });
		ev.Check($"{target}.endpoint-input", "C", Ok(echo), "CallEndpoint with an input body (echo)", required: true);

		var values = toolset
			? (await cli.CallAsync("ns_game_values", connect: pid)).Json
			: (await cli.CallAsync("run_csharp", new JsonObject { ["code"] = Scripts.GameValues }, connect: pid)).Json;
		ev.Step($"{target}.game-values", values);
		ev.Check($"{target}.game-values", "C", Ok(values) && Body(values).Contains("xp_per_level"), "game values load (rules.xp_per_level)", required: true);

		var secret = await Call("secret-probe");
		if (dedicated)
			ev.Check("dedicated.secret-key", "D", Ok(secret), "dedicated server with the secret key passes a requiresSecretKey endpoint", required: true);
		else
			ev.Check("editor.secret-gate", "D", !Ok(secret) && Code(secret).Contains("SECRET"), $"public client is refused a requiresSecretKey endpoint (got '{Code(secret)}')", required: true);

		if (!dedicated)
		{
			// Player-scoped writes need a player (steamId); the dedicated server has none.
			var first = await Call("init-player");
			var second = await Call("init-player");
			var created = Ok(first) || Code(first) == "PLAYER_EXISTS";
			ev.Check("editor.endpoint-error", "C", created && !Ok(second) && Code(second) == "PLAYER_EXISTS",
				$"endpoint error code reaches TryGetLastEndpointError (second init-player: '{Code(second)}', expected PLAYER_EXISTS)", required: true);
			var earn = await Call("earn");
			ev.Check("editor.ops-values", "C", Ok(earn) && Body(earn).Contains("100"), "inc op with {{values.rules.xp_per_level}} applied (earn granted 100)", required: true);
		}
		// Observations only (known findings, sbox-cool/sbox-network-storage#23): old-shape conditions and endpoint-only reads.
		var legacy = await Call("legacy-cond");
		ev.Observe($"{target}.legacy-cond", Ok(legacy) ? "ok" : Code(legacy));
	}

	record IdleStatus(JsonNode? Instance, JsonNode? Editor);

	static bool HasTool(CallResult search, string name) =>
		search.Json?["tools"] is JsonArray tools && tools.Any(t => J.Str(t, "name") == name);

	/// <summary>Source builds report version 0000000; record the sbox-public commit they were built from.</summary>
	static async Task<string?> EngineCommitAsync(string engine)
	{
		var r = await Proc.RunAsync("git", ["rev-parse", "HEAD"], engine, TimeSpan.FromSeconds(10));
		return r.Code == 0 ? r.Stdout.Trim() : null;
	}

	static async Task<IdleStatus> WaitForIdleAsync(SboxCli cli, string? pid, bool editor, int timeout, bool requireProject = false)
	{
		IdleStatus last = new(null, null);
		await WaitAsync(timeout, async () =>
		{
			var instance = (await cli.CallAsync("instance_status", connect: pid, timeout: 30)).Json;
			var editorStatus = editor ? (await cli.CallAsync("editor_status", connect: pid, timeout: 30)).Json : null;
			last = new(instance, editorStatus);
			var loaded = !requireProject || J.Str(instance, "project") is not null || J.Str(instance, "game") is not null;
			return loaded && instance?["isLoading"]?.GetValueKind() == JsonValueKind.False && (!editor || editorStatus?["isCompiling"]?.GetValueKind() == JsonValueKind.False);
		});
		return last;
	}

	static async Task<bool> WaitAsync(int seconds, Func<Task<bool>> condition)
	{
		var until = DateTime.UtcNow.AddSeconds(seconds);
		while (DateTime.UtcNow < until)
		{
			try { if (await condition()) return true; } catch { }
			await Task.Delay(3000);
		}
		return false;
	}

	/// <summary>The library's latest GitHub release tag (r&lt;revision&gt;, the current sbox.game revision), or null when there is none yet.</summary>
	static async Task<string?> LatestReleaseTagAsync()
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/sbox-cool/sbox-network-storage/releases/latest");
		request.Headers.UserAgent.ParseAdd("sbox-cli-e2e");
		if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token) request.Headers.Authorization = new("Bearer", token);
		using var response = await Http.Client.SendAsync(request);
		if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { Console.WriteLine("library has no GitHub release yet; using main"); return null; }
		response.EnsureSuccessStatusCode();
		return JsonNode.Parse(await response.Content.ReadAsStringAsync())?["tag_name"]?.GetValue<string>();
	}

	static async Task Git(string cwd, params string[] args)
	{
		var r = await Proc.RunAsync("git", args, cwd, TimeSpan.FromMinutes(5));
		if (r.Code != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.Stderr}");
	}
}

record Server(string Url, string ProjectId, string PublicKey, string SecretKey, NsCli? Cli, string? Commit);

sealed class Library
{
	public string? Version;
	public string Source = "";
	public string? Commit;
	public long? Revision;
	public string? DefaultApiRoot;
	public bool ConfigureTakesApiRoot;

	public static Library FromInstalled(string folder, string source)
	{
		var lib = new Library { Source = source };
		var client = Path.Combine(folder, "Code", "Core", "NetworkStorageClient.cs");
		var text = File.Exists(client) ? File.ReadAllText(client) : "";
		if (Regex.Match(text, "LibraryVersion\\s*=\\s*\"([^\"]+)\"") is { Success: true } m) lib.Version = m.Groups[1].Value;
		if (Regex.Match(text, @"ApiVersion\s*\{\s*get;\s*private set;\s*\}\s*=\s*""([^""]*)""") is { Success: true } root) lib.DefaultApiRoot = root.Groups[1].Value;
		lib.ConfigureTakesApiRoot = Regex.IsMatch(text, @"Configure\(\s*string projectId[^)]*\bapiVersion\b");
		// Library Manager writes .version as 1.0.<revision> on install; only trust it for package installs.
		var marker = Path.Combine(folder, ".version");
		if (source == "library-manager" && File.Exists(marker) && Regex.Match(File.ReadAllText(marker), @"^\d+\.\d+\.(\d+)") is { Success: true } r) lib.Revision = long.Parse(r.Groups[1].Value);
		return lib;
	}

	public JsonObject ToJson() => new() { ["version"] = Version ?? "legacy", ["source"] = Source, ["commit"] = Commit, ["revision"] = Revision };
}

static class Fixture
{
	static readonly string[] Kinds = ["endpoint", "collection", "workflow"];

	public static void WriteConfig(string fixture, Server server)
	{
		var root = Path.Combine(fixture, "Editor", "Network Storage", "config");
		Directory.CreateDirectory(Path.Combine(root, "public"));
		Directory.CreateDirectory(Path.Combine(root, "secret"));
		var config = new JsonObject
		{
			["projectId"] = server.ProjectId, ["publicKey"] = server.PublicKey, ["baseUrl"] = server.Url, ["cdnUrl"] = "", ["apiVersion"] = "v3",
			["dataFolder"] = "Network Storage", ["dataSource"] = "api_only", ["sourceExportMode"] = "source_only", ["enableAuthSessions"] = false,
			["enableEncryptedRequests"] = false, ["publishTarget"] = "live", ["proxyEnabled"] = false,
		};
		File.WriteAllText(Path.Combine(root, "public", "projectConfig.json"), config.ToJsonString(Evidence.Indented));
		File.WriteAllText(Path.Combine(root, "secret", "secret_key.json"), new JsonObject { ["secretKey"] = server.SecretKey }.ToJsonString(Evidence.Indented));
		File.WriteAllText(Path.Combine(fixture, "Assets", "network-storage.credentials.json"),
			new JsonObject { ["projectId"] = server.ProjectId, ["publicKey"] = server.PublicKey, ["baseUrl"] = server.Url, ["apiVersion"] = "v3" }.ToJsonString(Evidence.Indented));
	}

	/// <summary>Pushes the fixture's source files exactly as the Sync Tool sends them (raw source payloads), for runs without an editor.</summary>
	public static async Task PushSourcesAsync(string fixture, Server server, Evidence ev)
	{
		var batch = new JsonObject();
		foreach (var kind in Kinds)
		{
			var folder = Path.Combine(fixture, "Editor", "Network Storage", kind + "s");
			var resources = new JsonArray();
			if (Directory.Exists(folder))
				foreach (var file in Directory.GetFiles(folder).Where(f => f.EndsWith(".yml") || f.EndsWith(".yaml") || f.EndsWith(".json")).Order(StringComparer.Ordinal))
				{
					var name = Path.GetFileName(file);
					var id = Regex.Replace(name, $@"(\.{kind})?\.(ya?ml|json)$", "");
					resources.Add(new JsonObject
					{
						["kind"] = kind, ["id"] = id, ["authoringMode"] = "source", ["sourceFormat"] = name.EndsWith(".json") ? "json" : "yaml",
						["sourcePath"] = name, ["sourceText"] = File.ReadAllText(file),
					});
				}
			batch[kind + "s"] = resources;
		}
		using var request = new HttpRequestMessage(HttpMethod.Put, $"{server.Url}/v3/manage/{server.ProjectId}/sync") { Content = new StringContent(batch.ToJsonString(), Encoding.UTF8, "application/json") };
		request.Headers.Add("x-api-key", server.SecretKey);
		request.Headers.Add("x-public-key", server.PublicKey);
		using var response = await Http.Client.SendAsync(request);
		var body = await response.Content.ReadAsStringAsync();
		ev.Step("fixture.push(no editor)", new CallResult(response.IsSuccessStatusCode, body, Evidence.TryParse(body)));
		if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"pushing the fixture definitions failed: HTTP {(int)response.StatusCode} {body}");
	}
}

static class Scripts
{
	// Fallbacks for libraries without the network_storage toolset; same result shapes as the ns_* tools.
	public const string EditorSync = """
		SyncToolConfig.Load();
		var r = new System.Collections.Generic.Dictionary<string, object>();
		var v = await SyncToolApi.Validate();
		r["validate"] = v.HasValue ? "ok" : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
		var batch = new System.Collections.Generic.Dictionary<string, object>
		{
			["endpoints"] = SyncToolConfig.LoadSourcePayloadResources( "endpoint", includeDeprecated: false ),
			["collections"] = SyncToolConfig.LoadSourcePayloadResources( "collection" ),
			["workflows"] = SyncToolConfig.LoadSourcePayloadResources( "workflow" ),
		};
		var payload = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>( System.Text.Json.JsonSerializer.Serialize( batch ) );
		var pre = await SyncToolApi.PreflightSync( payload, "live" );
		r["preflight"] = pre.HasValue ? pre.Value.GetRawText() : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
		var push = await SyncToolApi.PushSync( payload, "live" );
		r["push"] = push.HasValue && push.Value.TryGetProperty( "ok", out var ok ) && ok.GetBoolean() ? "ok" : "ERR " + SyncToolApi.LastErrorCode + " " + SyncToolApi.LastErrorMessage;
		r["pass"] = (string)r["validate"] == "ok" && (string)r["push"] == "ok";
		return r;
		""";

	// No NetworkStorage.LibraryVersion here: libraries published before 1.1.0 do not have the constant, and the
	// snippet would not compile against them. The version comes from the library source instead.
	public static string Configure(Server s, string? apiRoot) => $$"""
		NetworkStorage.Configure( {{Lit(s.ProjectId)}}, {{Lit(s.PublicKey)}}, {{Lit(s.Url)}}{{(apiRoot is null ? "" : $", apiVersion: {Lit(apiRoot)}")}} );
		return new { allowed = Http.IsAllowed( new Uri( {{Lit(s.Url + "/v3/server-info")}} ) ) };
		""";

	public static string CallEndpoint(string slug, JsonObject? input) => $$"""
		object input = {{(input is null ? "null" : $"System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>( {Lit(input.ToJsonString())} )")}};
		var res = await NetworkStorage.CallEndpoint( {{Lit(slug)}}, input );
		NetworkStorage.TryGetLastEndpointError( {{Lit(slug)}}, out var code, out var message );
		return new { ok = res.HasValue, body = res?.GetRawText(), errorCode = res.HasValue ? null : code, errorMessage = res.HasValue ? null : message };
		""";

	public const string GameValues = """
		var res = await NetworkStorage.GetGameValues();
		return new { ok = res.HasValue, body = res?.GetRawText() };
		""";

	static string Lit(string value) => JsonSerializer.Serialize(value);
}

record CallResult(bool Ok, string Text, JsonNode? Json);

sealed class SboxCli(string exe, string dll, string cwd, string log)
{
	public async Task<Proc.Result> RunAsync(string[] args, int timeout = 120)
	{
		// Every call gets a hard deadline: a hung --connect otherwise blocks forever.
		var (file, prefix) = File.Exists(exe) ? (exe, Array.Empty<string>()) : ("dotnet", new[] { dll });
		var r = await Proc.RunAsync(file, [.. prefix, .. args], cwd, TimeSpan.FromSeconds(timeout + 15));
		File.AppendAllText(log, $"$ sbox-cli {string.Join(' ', args.Select(a => a.Length > 200 ? a[..200] + "…" : a))}\n[{r.Code}] {Trim(r.Stdout)}\n{Trim(r.Stderr)}\n");
		return r;
	}

	public async Task<CallResult> CallAsync(string tool, JsonObject? arguments = null, string? connect = null, int timeout = 120)
	{
		string[] args = connect is null ? ["--call", tool] : ["--connect", connect, "--call", tool];
		if (arguments is not null) args = [.. args, arguments.ToJsonString()];
		args = [.. args, "--request-timeout", timeout.ToString()];
		var r = await RunAsync(args, timeout);
		return new CallResult(r.Code == 0, r.Stdout.Trim(), Evidence.TryParse(r.Stdout));
	}

	static string Trim(string s) => s.Length > 4000 ? s[..4000] + "…" : s.Trim();
}

sealed class NsCli(string[] command, string config, string data)
{
	public string[] DirArgs => ["--config-dir", config, "--data-dir", data];
	public Task<Proc.Result> RunAsync(string[] args, string? stdin = null) =>
		Proc.RunAsync(command[0], [.. command[1..], .. args, .. DirArgs], Environment.CurrentDirectory, TimeSpan.FromMinutes(2), stdin);
}

static class Http
{
	public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
	public static async Task<bool> OkAsync(string url) { try { return (await Client.GetAsync(url)).IsSuccessStatusCode; } catch { return false; } }
	public static async Task<JsonNode?> GetJsonAsync(string url) => JsonNode.Parse(await Client.GetStringAsync(url));
}

static class Files
{
	/// <summary>Copies engine logs written during this run (the dedicated server logs to its own logs/ folder, not stdout).</summary>
	public static void CopyRecentLogs(string from, string to, DateTime since)
	{
		if (!Directory.Exists(from)) return;
		foreach (var file in Directory.EnumerateFiles(from, "*.log").Where(f => File.GetLastWriteTimeUtc(f) >= since))
		{
			Directory.CreateDirectory(to);
			try { File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true); } catch (IOException) { }
		}
	}

	public static void CopyDirectory(string from, string to)
	{
		foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(from, file);
			if (relative.Split(Path.DirectorySeparatorChar).Any(s => s is ".git" or "obj" or "bin")) continue;
			var destination = Path.Combine(to, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			File.Copy(file, destination, overwrite: true);
		}
	}
}

static class Proc
{
	public record Result(int Code, string Stdout, string Stderr);

	public static async Task<Result> RunAsync(string file, string[] args, string cwd, TimeSpan timeout, string? stdin = null)
	{
		var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
		foreach (var a in args) info.ArgumentList.Add(a);
		using var p = Process.Start(info)!;
		var stdout = p.StandardOutput.ReadToEndAsync();
		var stderr = p.StandardError.ReadToEndAsync();
		if (stdin is not null) await p.StandardInput.WriteAsync(stdin);
		p.StandardInput.Close();
		using var cts = new CancellationTokenSource(timeout);
		try { await p.WaitForExitAsync(cts.Token); }
		catch (OperationCanceledException) { Kill(p); return new Result(124, await stdout, await stderr + $"\ntimed out after {timeout.TotalSeconds:0}s"); }
		return new Result(p.ExitCode, await stdout, await stderr);
	}

	public static Process Start(string file, string[] args, string cwd, string log)
	{
		var info = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach (var a in args) info.ArgumentList.Add(a);
		var p = Process.Start(info)!;
		var writer = new StreamWriter(log, append: true) { AutoFlush = true };
		p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
		p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
		p.BeginOutputReadLine();
		p.BeginErrorReadLine();
		return p;
	}

	public static void Kill(Process p)
	{
		try { if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } } catch { }
	}
}

sealed class Evidence(string dir)
{
	public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
	public string Dir => dir;
	public readonly JsonObject Identity = new();
	readonly JsonArray checks = [];
	readonly JsonArray steps = [];
	readonly JsonObject observations = new();
	readonly List<string> failures = [];

	public bool Passed => failures.Count == 0 && checks.Count > 0;

	/// <summary>Parses a tool result with every object key camelCased, so engine (PascalCase) and library (camelCase) results read alike.</summary>
	public static JsonNode? TryParse(string text) { try { return J.Camel(JsonNode.Parse(text)); } catch { return null; } }

	public void Step(string name, CallResult r) => steps.Add(new JsonObject { ["step"] = name, ["ok"] = r.Ok, ["result"] = r.Json?.DeepClone() ?? Redact(r.Text) });
	public void Step(string name, JsonNode? r) => steps.Add(new JsonObject { ["step"] = name, ["result"] = r?.DeepClone() });
	public void Observe(string name, string value) => observations[name] = value;
	public void Fail(string name, string message) { failures.Add($"{name}: {message}"); checks.Add(new JsonObject { ["id"] = name, ["pass"] = false, ["detail"] = message, ["required"] = true }); }

	public void Check(string id, string section, bool pass, string detail, bool required)
	{
		checks.Add(new JsonObject { ["id"] = id, ["section"] = section, ["pass"] = pass, ["required"] = required, ["detail"] = detail });
		if (required && !pass) failures.Add($"{id}: {detail}");
		Console.WriteLine($"  [{(pass ? "x" : " ")}] {section} {id}: {detail}");
	}

	static string Redact(string text) => Regex.Replace(text, @"sbox_(ns|sk|pk)_[A-Za-z0-9_\-]+", "sbox_$1_…");

	JsonObject? row;

	public void WriteRow(Options o, string? engine, Library? lib)
	{
		var server = Identity["server"];
		var editorRan = o.Editor && Identity["engine"]?["target"]?.GetValue<string>() == "editor";
		var sections = string.Join(", ", checks.Where(c => c?["pass"]?.GetValue<bool>() == true).Select(c => c!["section"]?.GetValue<string>()).Where(s => s is not null).Distinct().Order());
		row = new JsonObject
		{
			["server"] = server?["version"]?.GetValue<string>(),
			["commit"] = server?["commit"]?.GetValue<string>() is { Length: >= 7 } c ? c[..12] : null,
			["kind"] = server?["kind"]?.GetValue<string>(),
			["library"] = lib?.Version,
			["librarySource"] = lib?.Source,
			["revision"] = lib?.Revision,
			["engine"] = engine,
			["os"] = Identity["os"]?.GetValue<string>(),
			["database"] = new JsonArray(server?["database"]?.GetValue<string>()),
			// A pass is an editor run (docs/editor-test-plan.md); a dedicated-only run is not.
			["result"] = !editorRan ? "ci-only" : Passed ? "pass" : "fail",
			["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
			["evidence"] = new JsonArray(o.EvidenceRefs.Select(e => (JsonNode)e).ToArray()),
			["notes"] = $"tools/sbox-cli-e2e ({o.Targets}{(editorRan ? "" : ", no editor run")}); sections exercised: {sections}; driver: {Identity["driver"] ?? Identity["dedicatedDriver"]}{(engine is null && Identity["engine"]?["commit"] is JsonNode engineCommit ? $"; engine: source build of sbox-public {engineCommit}" : "")}.",
		};
		foreach (var key in row.Where(p => p.Value is null).Select(p => p.Key).ToList()) row.Remove(key);
	}

	public void Save()
	{
		var doc = new JsonObject { ["identity"] = Identity.DeepClone(), ["pass"] = Passed, ["failures"] = new JsonArray(failures.Select(f => (JsonNode)f).ToArray()), ["checks"] = checks.DeepClone(), ["observations"] = observations.DeepClone(), ["steps"] = steps.DeepClone() };
		File.WriteAllText(Path.Combine(dir, "evidence.json"), doc.ToJsonString(Indented));
		if (row is not null) File.WriteAllText(Path.Combine(dir, "tested-row.json"), row.ToJsonString(Indented));
		var md = new StringBuilder("## sbox-cli e2e\n\n");
		md.AppendLine($"- Server: {Identity["server"]?["version"]} ({Identity["server"]?["kind"]}, {Identity["server"]?["database"]}) commit {Identity["server"]?["commit"]}");
		md.AppendLine($"- Library: {Identity["library"]?["version"]} from {Identity["library"]?["source"]} revision {Identity["library"]?["revision"]}");
		md.AppendLine($"- Engine: {Identity["engine"]?["version"]}{(Identity["engine"]?["commit"] is JsonNode engineCommit ? $" (sbox-public {engineCommit})" : "")} ({Identity["engine"]?["target"]}); OS: {Identity["os"]}");
		md.AppendLine($"- Result: **{(Passed ? "PASS" : "FAIL")}**\n");
		foreach (var c in checks) md.AppendLine($"- [{(c?["pass"]?.GetValue<bool>() == true ? "x" : " ")}] {c?["section"]} `{c?["id"]}`: {c?["detail"]}{(c?["required"]?.GetValue<bool>() == false ? " (informational)" : "")}");
		foreach (var (k, v) in observations) md.AppendLine($"- observation `{k}`: {v}");
		if (row is not null) md.AppendLine($"\nDraft `tested` row (add evidence links before committing):\n\n```json\n{row.ToJsonString(Indented)}\n```");
		File.WriteAllText(Path.Combine(dir, "summary.md"), md.ToString());
	}
}

static class J
{
	public static JsonNode? Camel(JsonNode? node) => node switch
	{
		JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key.Length > 0 ? char.ToLowerInvariant(p.Key[0]) + p.Key[1..] : p.Key, Camel(p.Value)))),
		JsonArray a => new JsonArray(a.Select(Camel).ToArray()),
		_ => node?.DeepClone(),
	};

	public static bool Bool(JsonNode? node, string key) => node is JsonObject o && o[key]?.GetValueKind() == JsonValueKind.True;
	public static string? Str(JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
