namespace MeuralImageBackup.Core.Files;

// MeuralImageUploader's folder and file names, kept exactly so existing files are found: the
// characters Windows doesn't allow in names are dropped, nothing else changes (not even a name
// that ends up empty). See "Compatibility" in CLAUDE.md.
public static class FileNaming
{
    // The characters removed from names: the ones invalid in Windows file names, plus the control
    // characters 1 to 31.
    internal static readonly char[] InvalidChars = BuildInvalidChars();

    // A frame's or gallery's folder; a missing name becomes "Unknown". With use false (the frame's
    // folder when USE_FRAME_NAME_IN_PATH=false) there's no folder: "".
    public static string FolderName(string? name, bool use = true)
    {
        if (!use)
            return "";
        if (string.IsNullOrEmpty(name))
            name = "Unknown";
        return Clean(name);
    }

    // An image's file name without the extension: its title, or the Meural file id when it has none,
    // or "Unknown" when it has neither.
    public static string FileName(string? title, int? fileId)
    {
        if (string.IsNullOrEmpty(title))
            title = fileId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";
        return Clean(title);
    }

    private static string Clean(string name) => new(name.Where(c => !InvalidChars.Contains(c)).ToArray());

    private static char[] BuildInvalidChars()
    {
        var chars = new List<char> { '"', '<', '>', '|', '\0' };
        for (var c = 1; c <= 31; c++)
            chars.Add((char)c);
        chars.AddRange([':', '*', '?', '\\', '/']);
        return [.. chars];
    }
}
