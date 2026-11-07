using System.Reflection;

namespace MeuralImageBackup.Core;

// The product name as people see it: page titles, the header. Change the name here only. Internal
// identifiers (namespaces, the Data Protection application name) are not the product name.
public static class AppInfo
{
    public const string Name = "MeuralImageBackup";

    // The running version, e.g. "2.0.0", stamped in at build time from the release tag; "dev" for
    // local builds. Read from the app (entry) assembly, where the host project sets it.
    public static string Version { get; } = ReadVersion();

    // The day the app was built, as "2026-11-06" (UTC), stamped in by the host project; null when
    // the build didn't record it.
    public static string? BuildDate { get; } = Assembly.GetEntryAssembly()?
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "BuildDate")?.Value;

    // What GET /api/version answers: four parts, like MeuralImageUploader's assembly version
    // ("1.1.1.0"). A release "2.0.0" gives "2.0.0.0"; a local "dev" build "0.0.0.0".
    public static string ApiVersion => FourPartVersion(Version);

    internal static string FourPartVersion(string version)
    {
        if (!System.Version.TryParse(version.Split('-')[0], out var v))
            return "0.0.0.0";
        return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}.{Math.Max(v.Revision, 0)}";
    }

    private static string ReadVersion()
    {
        var info = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(info) ? "dev" : info.Split('+')[0];
    }
}
