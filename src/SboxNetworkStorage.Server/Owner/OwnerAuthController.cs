using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerAuthModel(bool Setup, string? Token = null, string? Error = null, string? Username = null);

[EnableRateLimiting("owner-login")]
public sealed class OwnerAuthController(OwnerAccountService accounts, OwnerSetupToken setupToken) : Controller
{
    [HttpGet("/login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        if (await accounts.GetAsync(ct) is null)
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false, Error: "No owner exists yet. Use the local setup URL printed in the server log, or run sbox-ns admin create."));
        return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false));
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Login([FromForm] string? username, [FromForm] string? password, CancellationToken ct)
    {
        var owner = password is { Length: >= 1 and <= 1024 } && username is { Length: >= 1 and <= 64 }
            ? await accounts.AuthenticateAsync(username, password, ct) : null;
        if (owner is null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false, Error: "Invalid username or password.", Username: username));
        }
        await SignInAsync(owner);
        return Redirect("/dashboard");
    }

    [HttpGet("/setup")]
    public async Task<IActionResult> Setup([FromQuery] string? token, CancellationToken ct)
    {
        if (!IsLocal() || !setupToken.IsValid(token) || await accounts.GetAsync(ct) is not null) return NotFound();
        return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(true, token));
    }

    [HttpPost("/setup")]
    public async Task<IActionResult> Setup([FromForm] string? token, [FromForm] string? username,
        [FromForm] string? password, [FromForm] string? confirmPassword, CancellationToken ct)
    {
        if (!IsLocal() || !setupToken.IsValid(token) || await accounts.GetAsync(ct) is not null) return NotFound();
        try
        {
            if (password != confirmPassword) throw new ArgumentException("Passwords do not match.");
            var owner = await accounts.CreateAsync(username ?? string.Empty, password ?? string.Empty, ct);
            await SignInAsync(owner);
            return Redirect("/dashboard");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(true, token, ex.Message, username));
        }
    }

    [Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
    [HttpPost("/logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
        return Redirect("/login");
    }

    private bool IsLocal() => HttpContext.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);

    private Task SignInAsync(OwnerAccount owner)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, NetworkStorageServices.LocalOwnerUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, owner.Username),
            new Claim(OwnerHostingExtensions.StampClaim, owner.SecurityStamp)
        ], OwnerHostingExtensions.Scheme);
        return HttpContext.SignInAsync(OwnerHostingExtensions.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }
}
