namespace MeuralImageBackup.Core;

// The "what's new" banner, remembered per browser (there are no users): after an update the
// browser shows it until it's closed. A browser that has never stored a version starts at the
// running one, so nobody is told about a release they've been using all along. Local "dev" builds
// never announce anything. Any other version than the one seen counts, a lower one too: the
// versions were renumbered (2.1.0 to 2.5.0 became 2.0.0), so a browser that saw 2.5.0 still hears
// about 2.0.0 and everything after it.
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
        return Version.TryParse(seen, out var seenVersion) && seenVersion == runningVersion
            ? (null, false)
            : (running, false);
    }
}
