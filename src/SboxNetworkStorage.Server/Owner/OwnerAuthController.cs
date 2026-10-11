using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Server.Hosting;

namespace SboxNetworkStorage.Server.Owner;

/// <param name="NoOwner">Login page on a server without an owner: shows the steps to create one instead of the form.</param>
public sealed record OwnerAuthModel(bool Setup, string? Token = null, string? Error = null, string? Username = null, bool NoOwner = false);
public sealed record OwnerLoginLinkModel(string? Token, string? OwnerName, string? Error = null, string? Username = null)
{
    public bool CreatesOwner => Token is not null && OwnerName is null;
}

[EnableRateLimiting(OwnerLoginLimits.Policy)]
public sealed class OwnerAuthController(OwnerAccountService accounts, OwnerSetupToken setupToken,
    OwnerLoginLinkService loginLinks, OwnerTurnstile turnstile, ILogger<OwnerAuthController> logger) : Controller
{
    [HttpGet("/login")]
    public async Task<IActionResult> Login(CancellationToken ct)
    {
        if (OwnerTransport.RefusesCredentials(HttpContext)) return InsecureHttpRefused();
        if (await accounts.GetAsync(ct) is null)
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false, NoOwner: true));
        return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false));
    }

    [HttpPost("/login")]
    public async Task<IActionResult> Login([FromForm] string? username, [FromForm] string? password,
        [FromForm] string? secondFactor, [FromForm(Name = "cf-turnstile-response")] string? verification, CancellationToken ct)
    {
        if (OwnerTransport.RefusesCredentials(HttpContext)) return InsecureHttpRefused();
        if (!await turnstile.VerifyAsync(verification, "owner_login", HttpContext.Connection.RemoteIpAddress, ct)) return StatusCode(403);
        var owner = password is { Length: >= 1 and <= 1024 } && username is { Length: >= 1 and <= 64 }
            ? await accounts.AuthenticateAsync(username, password, ct) : null;
        bool secondFactorOk;
        try
        {
            secondFactorOk = owner is not null && (owner.TotpSecret is null || await accounts.VerifySecondFactorAsync(owner.SecurityStamp, secondFactor, ct));
        }
        catch (OwnerAuthenticatorUnavailableException ex)
        {
            logger.LogWarning(ex, "Owner authenticator secret cannot be decrypted with this server's key ring (restored from another machine?). Run sbox-ns admin reset-2fa on the server.");
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false,
                Error: "This server cannot read your authenticator, for example after a restore on another machine. Sign in with a recovery code, or run sbox-ns admin reset-2fa on the server and sign in with your password.",
                Username: username));
        }

        if (owner is null || !secondFactorOk)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(false, Error: "Invalid credentials or authenticator/recovery code.", Username: username));
        }
        await SignInAsync(owner);
        OwnerFlash.Success(this, "Signed in.");
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
        [FromForm] string? password, [FromForm] string? confirmPassword,
        [FromForm(Name = "cf-turnstile-response")] string? verification, CancellationToken ct)
    {
        if (!IsLocal() || !setupToken.IsValid(token) || await accounts.GetAsync(ct) is not null) return NotFound();
        if (!await turnstile.VerifyAsync(verification, "owner_setup", HttpContext.Connection.RemoteIpAddress, ct)) return StatusCode(403);
        try
        {
            if (password != confirmPassword) throw new ArgumentException("Passwords do not match.");
            var owner = await accounts.CreateAsync(username ?? string.Empty, password ?? string.Empty, ct);
            await SignInAsync(owner);
            OwnerFlash.Success(this, "Owner account created. Signed in.");
            return Redirect("/dashboard");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return View("~/Views/Owner/Auth.cshtml", new OwnerAuthModel(true, token, ex.Message, username));
        }
    }

    // GET never consumes the token, so link-preview bots and chat unfurlers cannot burn it.
    [HttpGet("/login/link")]
    public async Task<IActionResult> LoginLink([FromQuery] string? token, CancellationToken ct)
    {
        if (OwnerTransport.RefusesCredentials(HttpContext)) return InsecureHttpRefused();
        if (!await loginLinks.IsValidAsync(token, ct)) return LoginLinkRejected();
        var owner = await accounts.GetAsync(ct);
        return View(LoginLinkView, new OwnerLoginLinkModel(token, owner?.Username));
    }

    [HttpPost("/login/link")]
    public async Task<IActionResult> LoginLink([FromForm] string? token, [FromForm] string? username,
        [FromForm] string? password, [FromForm] string? confirmPassword,
        [FromForm(Name = "cf-turnstile-response")] string? verification, CancellationToken ct)
    {
        if (OwnerTransport.RefusesCredentials(HttpContext)) return InsecureHttpRefused();
        if (!await loginLinks.IsValidAsync(token, ct)) return LoginLinkRejected();
        if (!await turnstile.VerifyAsync(verification, "owner_link", HttpContext.Connection.RemoteIpAddress, ct)) return StatusCode(403);
        var owner = await accounts.GetAsync(ct);
        if (owner is not null)
        {
            if (!await loginLinks.TryConsumeAsync(token, ct)) return LoginLinkRejected();
        }
        else
        {
            // Shell access proved authority, so this path may create the first owner remotely.
            try
            {
                if (password != confirmPassword) throw new ArgumentException("Passwords do not match.");
                OwnerAccountService.ValidateCredentials(username ?? string.Empty, password ?? string.Empty);
                if (!await loginLinks.TryConsumeAsync(token, ct)) return LoginLinkRejected();
                owner = await accounts.CreateAsync(username!, password!, ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                var stillValid = await loginLinks.IsValidAsync(token, ct);
                return View(LoginLinkView, new OwnerLoginLinkModel(stillValid ? token : null, null,
                    stillValid ? ex.Message : ex.Message + " This link has been used; run sbox-ns admin login-link again.", username));
            }
        }
        logger.LogWarning("Owner '{Username}' signed in with a single-use login link from {RemoteAddress}.",
            owner.Username, HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        await SignInAsync(owner);
        OwnerFlash.Success(this, "Signed in.");
        return Redirect("/dashboard");
    }

    [Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
    [HttpPost("/logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        // A new stamp also ends copies of this cookie (and any other session); there is only one owner.
        await accounts.RevokeSessionsAsync(ct);
        await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
        OwnerFlash.Success(this, "Signed out.");
        return Redirect("/login");
    }

    private const string LoginLinkView = "~/Views/Owner/LoginLink.cshtml";

    private ViewResult LoginLinkRejected()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View(LoginLinkView, new OwnerLoginLinkModel(null, null,
            "This login link is invalid, expired or already used. On the server run sbox-ns admin login-link for a new one."));
    }

    /// <summary>Plain HTTP from another machine: explain the safe ways in instead of taking a password or link.</summary>
    private ViewResult InsecureHttpRefused()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("~/Views/Owner/InsecureHttp.cshtml");
    }

    private bool IsLocal() => OwnerTransport.IsLoopback(HttpContext);

    private Task SignInAsync(OwnerAccount owner)
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, NetworkStorageServices.LocalOwnerUserId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, owner.Username),
            new Claim(OwnerHostingExtensions.StampClaim, owner.SecurityStamp)
        ], OwnerHostingExtensions.Scheme);
        return HttpContext.SignInAsync(OwnerHostingExtensions.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = false });
    }
}
