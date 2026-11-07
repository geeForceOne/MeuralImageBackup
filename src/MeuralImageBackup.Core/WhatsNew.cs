namespace MeuralImageBackup.Core;

// The "what's new" banner, remembered per browser (there are no users): after an update the
// browser shows it until it's closed. A browser that has never stored a version starts at the
// running one, so nobody is told about a release they've been using all along. Local "dev" builds
// never announce anything.
public static class WhatsNew
{
    // What to do with the version the browser stored (null: none yet): Show is the version to
    // announce or null; Store is true when the browser should remember the running version now.
    public static (string? Show, bool Store) Decide(string? seen, string running)
    {
        if (!Version.TryParse(running, out var runningVersion))
            return (null, false);
        if (string.IsNullOrEmpty(seen))
            return (null, true);
        return Version.TryParse(seen, out var seenVersion) && seenVersion >= runningVersion
            ? (null, false)
            : (running, false);
    }
}
