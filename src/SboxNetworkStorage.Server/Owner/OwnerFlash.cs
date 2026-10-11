using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// One-shot toast notifications carried by MVC TempData (cookie provider,
/// protected by the configured Data Protection keys). Controllers call
/// <see cref="Success"/> (or <see cref="Info"/>) before redirecting;
/// <c>Views/Owner/_Layout.cshtml</c> renders them once inside a polite live
/// region, and <c>owner-toast.js</c> auto-dismisses them. Secrets never pass
/// through flashes; one-time keys keep the existing server-side handoff.
/// </summary>
public static class OwnerFlash
{
    private const string Key = "OwnerFlash";

    public sealed record FlashMessage(string Kind, string Text);

    /// <summary>Name what changed after a successful state-changing action.</summary>
    public static void Success(Controller controller, string text) => Add(controller, "success", text);

    /// <summary>Neutral follow-ups (imports, background work) that deserve a toast.</summary>
    public static void Info(Controller controller, string text) => Add(controller, "info", text);

    private static void Add(Controller controller, string kind, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (text.Length > 240) text = text[..240];
        var list = Peek(controller.TempData);
        list.Add(new FlashMessage(kind, text));
        controller.TempData[Key] = JsonSerializer.Serialize(list);
    }

    private static List<FlashMessage> Peek(ITempDataDictionary tempData)
    {
        if (tempData.TryGetValue(Key, out var raw) && raw is string json)
        {
            try
            {
                return JsonSerializer.Deserialize<List<FlashMessage>>(json) ?? [];
            }
            catch (JsonException)
            {
            }
        }
        return [];
    }

    /// <summary>Reads and consumes the pending flashes; a second read returns none.</summary>
    public static IReadOnlyList<FlashMessage> Take(ITempDataDictionary tempData)
    {
        var list = Peek(tempData);
        tempData.Remove(Key);
        return list;
    }
}
