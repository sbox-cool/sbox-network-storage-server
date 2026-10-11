using Microsoft.AspNetCore.Mvc;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Two-step confirmation for destructive dashboard actions when JavaScript is
/// unavailable. Destructive forms carry <c>confirmTitle</c>/<c>confirmMessage</c>
/// hidden fields; with JavaScript the shared confirm dialog asks and resubmits
/// with <c>confirmed=true</c>. Without it the POST first renders the shared
/// confirm page, which reposts every field plus <c>confirmed=true</c>.
/// Typed-name confirmations (project and record deletes) do not use this.
/// </summary>
public static class OwnerConfirm
{
    public sealed record ConfirmModel(string Title, string Message);

    /// <summary>True when the request already passed confirmation.</summary>
    public static bool IsConfirmed(HttpRequest request)
        => request.HasFormContentType && string.Equals(request.Form["confirmed"], "true", StringComparison.Ordinal);

    /// <summary>Render the shared confirm page echoing the posted fields.</summary>
    public static IActionResult Page(Controller controller)
    {
        var form = controller.Request.HasFormContentType ? controller.Request.Form : null;
        var title = form?["confirmTitle"].ToString().Trim() ?? "";
        var message = form?["confirmMessage"].ToString().Trim() ?? "";
        if (title.Length == 0) title = "Confirm this action";
        return controller.View("~/Views/Owner/Confirm.cshtml", new ConfirmModel(title, message));
    }
}
