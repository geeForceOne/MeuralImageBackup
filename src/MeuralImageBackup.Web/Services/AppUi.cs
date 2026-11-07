using MeuralImageBackup.Web.Components.Shared;

namespace MeuralImageBackup.Web.Services;

// The layout's shared dialogs and the browser's time zone, cascaded to every page so each one can
// await a confirmation, show a toast or format a date without declaring its own.
public sealed class AppUi
{
    public ConfirmDialog Confirm { get; internal set; } = default!;
    public PromptDialog Prompt { get; internal set; } = default!;
    public Toast Toast { get; internal set; } = default!;

    // The browser's time zone (sent along as a cookie), or the server's when it's not known yet.
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    public static TimeZoneInfo FindZone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            return zone;
        return TimeZoneInfo.Local;
    }
}
