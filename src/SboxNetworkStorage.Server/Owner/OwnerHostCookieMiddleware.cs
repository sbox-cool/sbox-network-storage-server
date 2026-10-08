using Microsoft.Net.Http.Headers;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Over HTTPS the owner session and antiforgery cookies travel as <c>__Host-</c> cookies (Secure, Path=/, no Domain),
/// so a sibling subdomain of a shared parent domain cannot toss replacements onto it. ASP.NET Core fixes one cookie
/// name per option set (and the antiforgery token store is not replaceable), so this middleware translates between
/// the logical names the framework uses and the prefixed wire names. Plain-HTTP (loopback/IP) requests keep the
/// unprefixed names because browsers reject <c>__Host-</c> cookies without Secure.
/// </summary>
public sealed class OwnerHostCookieMiddleware(RequestDelegate next)
{
    public const string Prefix = "__Host-";

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.IsHttps) return next(context);
        TranslateRequestCookies(context.Request);
        context.Response.OnStarting(static state =>
        {
            PrefixResponseCookies((HttpResponse)state);
            return Task.CompletedTask;
        }, context.Response);
        return next(context);
    }

    /// <summary>Logical owner cookie names, including the chunks (<c>sbox-ns-ownerC1</c>…) of a chunked session cookie.</summary>
    public static bool IsOwnerCookie(string name)
        => name == OwnerHostingExtensions.CsrfCookieName || name.StartsWith(OwnerHostingExtensions.AuthCookieName, StringComparison.Ordinal);

    private static void TranslateRequestCookies(HttpRequest request)
    {
        var header = request.Headers.Cookie;
        if (!header.Any(value => value?.Contains("sbox-ns-", StringComparison.Ordinal) == true)) return;
        var kept = new List<string>();
        foreach (var value in header)
        {
            foreach (var part in (value ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                var name = separator < 0 ? part : part[..separator];
                if (name.StartsWith(Prefix, StringComparison.Ordinal) && IsOwnerCookie(name[Prefix.Length..]))
                    kept.Add(part[Prefix.Length..]);
                // An unprefixed owner cookie over HTTPS may have been planted by a sibling domain: ignore it.
                else if (!IsOwnerCookie(name))
                    kept.Add(part);
            }
        }
        request.Headers.Cookie = string.Join("; ", kept);
    }

    private static void PrefixResponseCookies(HttpResponse response)
    {
        var values = response.Headers.SetCookie;
        string?[]? rewritten = null;
        for (var index = 0; index < values.Count; index++)
        {
            if (!SetCookieHeaderValue.TryParse(values[index], out var cookie) || cookie.Name.Value is not { } name || !IsOwnerCookie(name))
                continue;
            cookie.Name = Prefix + name;
            cookie.Secure = true;
            cookie.Path = "/";
            cookie.Domain = null;
            (rewritten ??= values.ToArray())[index] = cookie.ToString();
        }
        if (rewritten is not null) response.Headers.SetCookie = rewritten;
    }
}
