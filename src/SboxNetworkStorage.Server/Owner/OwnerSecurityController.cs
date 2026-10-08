using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace SboxNetworkStorage.Server.Owner;

public sealed record OwnerSecurityModel(bool Enabled, string? Secret = null, string? Enrollment = null,
    string? Error = null, string[]? RecoveryCodes = null);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
[EnableRateLimiting("owner-login")]
public sealed class OwnerSecurityController(OwnerAccountService accounts) : Controller
{
    [HttpGet("/dashboard/security")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var owner = await accounts.GetAsync(ct);
        if (owner is null) return Unauthorized();
        if (owner.TotpSecret is not null) return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(true));
        var secret = OwnerTotp.NewSecret();
        return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(false, secret, accounts.ProtectEnrollment(secret)));
    }

    [HttpPost("/dashboard/security/enroll")]
    public async Task<IActionResult> Enroll([FromForm] string? password, [FromForm] string? enrollment,
        [FromForm] string? code, CancellationToken ct)
    {
        var owner = password is { Length: >= 1 and <= 1024 }
            ? await accounts.AuthenticateAsync(User.Identity!.Name!, password, ct) : null;
        if (owner is null || owner.SecurityStamp != User.FindFirstValue(OwnerHostingExtensions.StampClaim)) return Unauthorized();
        try
        {
            var codes = await accounts.EnrollAsync(owner.SecurityStamp, enrollment ?? string.Empty, code ?? string.Empty, ct);
            await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
            return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(true, RecoveryCodes: codes));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(false, Error: ex.Message));
        }
    }
}
