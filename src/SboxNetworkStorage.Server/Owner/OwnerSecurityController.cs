using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SboxNetworkStorage.Application.Common;
using SboxNetworkStorage.Application.Workspace;

namespace SboxNetworkStorage.Server.Owner;

/// <param name="EnrollmentUri">The <c>otpauth://</c> link for <paramref name="Secret"/> that authenticator apps open.</param>
/// <param name="ReadOnly">Demo servers show the page without the forms, which the read-only middleware refuses anyway.</param>
public sealed record OwnerSecurityModel(bool Enabled, string? Secret = null, string? Enrollment = null,
    string? Error = null, string[]? RecoveryCodes = null, string? EnrollmentUri = null, bool ReadOnly = false);

[Authorize(AuthenticationSchemes = OwnerHostingExtensions.Scheme)]
[EnableRateLimiting(OwnerLoginLimits.Policy)]
public sealed class OwnerSecurityController(OwnerAccountService accounts, IWorkspaceStore workspace, IAuditLogger audit,
    ILogger<OwnerSecurityController> logger) : Controller
{
    private const string UnreadableAuthenticator = "This server cannot read your authenticator. Use a recovery code, or run sbox-ns admin reset-2fa on the server.";

    [HttpGet("/dashboard/security")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (HttpContext.Items.ContainsKey(Demo.DemoReadOnlyMiddleware.ContextKey))
            return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(true, ReadOnly: true));
        var model = await PageAsync(null, ct);
        return model is null ? Unauthorized() : View("~/Views/Owner/Security.cshtml", model);
    }

    [HttpPost("/dashboard/security/enroll")]
    public async Task<IActionResult> Enroll([FromForm] string? password, [FromForm] string? enrollment,
        [FromForm] string? code, CancellationToken ct)
    {
        var owner = password is { Length: >= 1 and <= 1024 }
            ? await accounts.AuthenticateAsync(User.Identity!.Name!, password, ct) : null;
        if (owner is null || owner.SecurityStamp != StampClaim()) return Unauthorized();
        try
        {
            var codes = await accounts.EnrollAsync(owner.SecurityStamp, enrollment ?? string.Empty, code ?? string.Empty, ct);
            await AuditAsync("owner.2fa.enable", "Enabled two-factor sign-in", ct);
            await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
            return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(true, RecoveryCodes: codes));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Response.StatusCode = 400;
            return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(false, Error: ex.Message));
        }
    }

    /// <summary>Changes the password. Needs the current password and, when enabled, a second factor. Ends every session.</summary>
    [HttpPost("/dashboard/security/password")]
    public async Task<IActionResult> ChangePassword([FromForm] string? currentPassword, [FromForm] string? secondFactor,
        [FromForm] string? newPassword, [FromForm] string? confirmPassword, CancellationToken ct)
    {
        if (newPassword != confirmPassword) return await ErrorAsync("The new passwords do not match.", ct);
        try
        {
            await accounts.ChangePasswordAsync(StampClaim(), currentPassword, secondFactor, newPassword ?? string.Empty, ct);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return await ErrorAsync(Refusal(ex), ct);
        }
        await AuditAsync("owner.password.change", "Changed the owner password", ct);
        await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
        OwnerFlash.Success(this, "Password changed. Sign in with your new password.");
        return Redirect("/login");
    }

    /// <summary>Turns two-factor sign-in off. Needs the current password and a second factor. Ends every session.</summary>
    [HttpPost("/dashboard/security/2fa/disable")]
    public async Task<IActionResult> DisableSecondFactor([FromForm] string? currentPassword, [FromForm] string? secondFactor, CancellationToken ct)
    {
        try
        {
            await accounts.DisableSecondFactorAsync(StampClaim(), currentPassword, secondFactor, ct);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return await ErrorAsync(Refusal(ex), ct);
        }
        await AuditAsync("owner.2fa.disable", "Turned off two-factor sign-in", ct);
        await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
        OwnerFlash.Success(this, "Two-factor sign-in is off. Sign in with your password. Set up an authenticator again from Security to turn it back on.");
        return Redirect("/login");
    }

    /// <summary>Replaces the recovery codes. Needs the current password and a second factor. The new codes are shown once, and every session ends.</summary>
    [HttpPost("/dashboard/security/recovery-codes")]
    public async Task<IActionResult> RegenerateRecoveryCodes([FromForm] string? currentPassword, [FromForm] string? secondFactor, CancellationToken ct)
    {
        string[] codes;
        try
        {
            codes = await accounts.RegenerateRecoveryCodesAsync(StampClaim(), currentPassword, secondFactor, ct);
        }
        catch (Exception ex) when (IsRefusal(ex))
        {
            return await ErrorAsync(Refusal(ex), ct);
        }
        await AuditAsync("owner.recovery_codes.regenerate", "Regenerated the recovery codes", ct);
        await HttpContext.SignOutAsync(OwnerHostingExtensions.Scheme);
        return View("~/Views/Owner/Security.cshtml", new OwnerSecurityModel(true, RecoveryCodes: codes));
    }

    private string StampClaim() => User.FindFirstValue(OwnerHostingExtensions.StampClaim) ?? string.Empty;

    /// <summary>The page for the current account state. Null when no owner exists.</summary>
    private async Task<OwnerSecurityModel?> PageAsync(string? error, CancellationToken ct)
    {
        var owner = await accounts.GetAsync(ct);
        if (owner is null) return null;
        if (owner.TotpSecret is not null) return new OwnerSecurityModel(true, Error: error);
        var secret = OwnerTotp.NewSecret();
        return new OwnerSecurityModel(false, secret, accounts.ProtectEnrollment(secret), Error: error,
            EnrollmentUri: OwnerTotp.EnrollmentUri(secret, owner.Username));
    }

    private async Task<IActionResult> ErrorAsync(string error, CancellationToken ct)
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        var model = await PageAsync(error, ct);
        return model is null ? Unauthorized() : View("~/Views/Owner/Security.cshtml", model);
    }

    private static bool IsRefusal(Exception ex)
        => ex is OwnerReauthenticationException or OwnerAuthenticatorUnavailableException or ArgumentException or InvalidOperationException;

    private static string Refusal(Exception ex)
        => ex is OwnerAuthenticatorUnavailableException ? UnreadableAuthenticator : ex.Message;

    /// <summary>
    /// Server-wide account changes are recorded in each project's audit log, as whole-server exports are. Passwords, codes and
    /// secrets are never part of the entry. A server with no projects keeps only the warning log line.
    /// </summary>
    private async Task AuditAsync(string action, string message, CancellationToken ct)
    {
        var owner = OwnerProjectScope.Owner;
        foreach (var project in await workspace.GetUserProjectsAsync(owner, ct))
        {
            await audit.LogActionAsync(new AuditLogRequest(ProjectId: project.Id,
                UserId: owner.ToString(CultureInfo.InvariantCulture), Action: action,
                Actor: new { id = owner, type = "owner-dashboard", name = User.Identity?.Name },
                Target: new { id = project.Id, type = "project" }, Summary: new { message }, Before: null, After: null), ct);
        }
        logger.LogWarning("Owner {Owner} performed {Action} from {RemoteAddress}", User.Identity?.Name, action,
            HttpContext.Connection.RemoteIpAddress);
    }
}
