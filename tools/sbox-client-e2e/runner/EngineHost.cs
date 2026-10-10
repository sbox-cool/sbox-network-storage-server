global using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>Boots the headless engine once per test run (FACEPUNCH_ENGINE points at the engine's game folder).</summary>
[TestClass]
public class EngineHost
{
	static Sandbox.TestAppSystem _app;

	[AssemblyInitialize]
	public static void Start( TestContext context )
	{
		_app = new Sandbox.TestAppSystem();
		_app.Init();
	}

	[AssemblyCleanup]
	public static void Stop() => _app.Shutdown();
}
