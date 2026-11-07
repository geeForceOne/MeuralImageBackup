using System.Globalization;

namespace MeuralImageBackup.Web.Services;

// Numbers and dates the way the browser's region format writes them (the request's culture), in
// the browser's time zone. The words are always US English.
public static class Format
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string Number(int value) => value.ToString("N0", C);

    // "3 images", with the number in the region format.
    public static string Count(int count, string one, string many) => $"{Number(count)} {(count == 1 ? one : many)}";

    public static string Date(DateTime utc, TimeZoneInfo zone) => Local(utc, zone).ToString("d", C);

    public static string DateTime(DateTime utc, TimeZoneInfo zone) => Local(utc, zone).ToString("g", C);

    // "3 days ago", or the date once it's long ago.
    public static string Ago(DateTime utc, TimeZoneInfo zone)
    {
        var span = System.DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return Count((int)span.TotalMinutes, "minute ago", "minutes ago");
        if (span.TotalDays < 1) return Count((int)span.TotalHours, "hour ago", "hours ago");
        if (span.TotalDays < 60) return Count((int)span.TotalDays, "day ago", "days ago");
        return Date(utc, zone);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", C) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", C) + " MB",
        _ => Math.Max(1, bytes / 1024).ToString(C) + " KB",
    };

    private static DateTime Local(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(System.DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
}
