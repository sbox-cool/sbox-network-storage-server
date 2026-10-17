namespace SboxNetworkStorage.Server.Cli;

/// <summary>
/// The setup questions behind <c>adminpanel.allow_insecure_http</c>: plain-HTTP owner sign-in is an explicit opt-in,
/// offered only to operators with no better way in (no domain or sboxns.com name for HTTPS, no SSH tunnel).
/// Game traffic is unaffected either way.
/// </summary>
internal static class HttpOptIn
{
    internal const string Key = "adminpanel.allow_insecure_http";

    /// <summary>Returns the value to store for <see cref="Key"/>.</summary>
    /// <param name="current">The value already configured (the default offered).</param>
    /// <param name="flag"><c>--allow-http</c>: opt in without asking.</param>
    /// <param name="httpsConfigured">HTTPS already reaches the panel (https public URL, TLS, tunnel or sboxns.com DNS).</param>
    internal static bool Decide(bool current, bool flag, bool httpsConfigured, bool interactive, int port,
        TextWriter output, Func<string, bool, bool> askYesNo)
    {
        if (flag) return true;
        if (httpsConfigured || !interactive) return current;

        output.WriteLine();
        output.WriteLine("Web panel sign-in");
        output.WriteLine("  The owner panel asks for your password. By default the server refuses it over plain HTTP");
        output.WriteLine("  from other machines, because anyone on the network path could read it. Game traffic is not affected.");

        if (askYesNo("Do you have a domain name for this server, or will you use the sboxns.com DNS service?", false))
        {
            output.WriteLine("  Good: after setup run `sbox-ns dns enable` (sboxns.com name) or set tls.mode (your own domain).");
            output.WriteLine(StaysLine(current));
            return current;
        }

        if (askYesNo("Can you reach the panel through an SSH tunnel instead?", false))
        {
            output.WriteLine($"  Good: from your computer run `ssh -L {port}:127.0.0.1:{port} user@your-server`, then open");
            output.WriteLine($"  http://localhost:{port}/login.");
            output.WriteLine(StaysLine(current));
            return current;
        }

        output.WriteLine("  Without a domain or an SSH tunnel you need plain HTTP to use the web panel. That is opt-in:");
        output.WriteLine("  your password, login links and session cookie then travel unencrypted. It is fine for development");
        output.WriteLine("  and early game publishing; we recommend switching to HTTPS as soon as you can for long-term security.");
        output.WriteLine("  The panel shows a dismissable warning while HTTP is enabled, and you can turn it off any time:");
        output.WriteLine($"  sbox-ns config set {Key} false");
        return askYesNo("Allow the web panel over plain HTTP?", current);
    }

    private static string StaysLine(bool current)
        => current
            ? $"  Plain HTTP is still enabled; turn it off with `sbox-ns config set {Key} false` once HTTPS works."
            : "  Plain HTTP stays off.";
}
