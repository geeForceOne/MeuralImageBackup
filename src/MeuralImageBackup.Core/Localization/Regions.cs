using System.Globalization;

namespace MeuralImageBackup.Core.Localization;

// The region formats and time zones the Settings page offers (taken over from Trovami). Without a
// choice, both come from the browser; de-CH stays the fallback, as in MeuralImageUploader.
public static class Regions
{
    public const string DefaultRegion = "de-CH";

    public static readonly IReadOnlyList<string> RegionFormats =
    [
        "de-CH", "de-DE", "de-AT", "de-LI", "fr-CH", "it-CH",
        "en-US", "en-GB", "en-IE", "en-CA", "en-AU",
        "fr-FR", "it-IT", "es-ES", "nl-NL",
    ];

    public static bool IsRegionFormat(string? region) => region is not null && RegionFormats.Contains(region);

    public static CultureInfo Culture(string? region) => CultureInfo.GetCultureInfo(IsRegionFormat(region) ? region! : DefaultRegion);

    // Zones that must always be offered. Windows lists one zone per Windows id (so its
    // "W. Europe Standard Time" becomes only Europe/Berlin), but resolves these IANA ids through ICU.
    private static readonly string[] CommonTimeZones =
    [
        "Europe/Zurich", "Europe/Vaduz", "Europe/Berlin", "Europe/Vienna", "Europe/Paris", "Europe/Rome", "Europe/Milan",
        "Europe/London", "Europe/Dublin", "Europe/Amsterdam", "Europe/Brussels", "Europe/Luxembourg", "Europe/Madrid",
        "Europe/Lisbon", "Europe/Stockholm", "Europe/Oslo", "Europe/Copenhagen", "Europe/Helsinki", "Europe/Warsaw",
        "Europe/Prague", "Europe/Budapest", "Europe/Athens", "Europe/Istanbul", "Europe/Moscow",
        "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles", "America/Toronto", "America/Sao_Paulo",
        "Asia/Dubai", "Asia/Kolkata", "Asia/Singapore", "Asia/Shanghai", "Asia/Tokyo", "Australia/Sydney", "Pacific/Auckland",
        "Africa/Johannesburg", "UTC",
    ];

    // IANA ids of the time zones this machine knows, sorted.
    public static IReadOnlyList<string> TimeZoneIds => TimeZoneIdList.Value;

    // The list for a dropdown: always contains `current`, so a saved zone is never shown (and
    // saved) as a different one.
    public static IReadOnlyList<string> TimeZoneIdsWith(string? current) =>
        current is null || TimeZoneIds.Contains(current) || !IsKnownTimeZone(current)
            ? TimeZoneIds
            : TimeZoneIds.Append(current).Order(StringComparer.Ordinal).ToList();

    private static readonly Lazy<IReadOnlyList<string>> TimeZoneIdList = new(() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(tz => tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : null)
            .OfType<string>()
            .Where(id => id.Contains('/') || id == "UTC")
            .Concat(CommonTimeZones.Where(IsKnownTimeZone))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList());

    public static bool IsKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }
}
