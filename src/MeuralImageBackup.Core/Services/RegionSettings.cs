using MeuralImageBackup.Core.Localization;

namespace MeuralImageBackup.Core.Services;

// The region format (numbers and dates) and time zone chosen on the Settings page, for the whole
// installation (the Settings table). Null means automatic: from the browser. Read on every request
// that opens a page, so they're kept in memory until changed.
public sealed class RegionSettings(SettingsService settings)
{
    public const string RegionKey = "RegionFormat";
    public const string TimeZoneKey = "TimeZone";

    private (string? Region, string? TimeZone)? _cached;

    public async Task<(string? Region, string? TimeZone)> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } c) return c;
        var region = await settings.GetAsync(RegionKey, ct);
        var zone = await settings.GetAsync(TimeZoneKey, ct);
        var value = (Regions.IsRegionFormat(region) ? region : null, Regions.IsKnownTimeZone(zone) ? zone : null);
        _cached = value;
        return value;
    }

    // Null (or an unknown value) for automatic.
    public async Task SetAsync(string? region, string? timeZone, CancellationToken ct = default)
    {
        region = Regions.IsRegionFormat(region) ? region : null;
        timeZone = Regions.IsKnownTimeZone(timeZone) ? timeZone : null;
        await settings.SetAsync(RegionKey, region, ct);
        await settings.SetAsync(TimeZoneKey, timeZone, ct);
        _cached = (region, timeZone);
    }
}
