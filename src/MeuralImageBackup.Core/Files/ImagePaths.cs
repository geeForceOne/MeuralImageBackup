namespace MeuralImageBackup.Core.Files;

// Where an image's file is: uploads/<frame>/<gallery>/<title>.jpeg, or uploads/<gallery>/<title>.jpeg
// without the frame's folder (USE_FRAME_NAME_IN_PATH=false). Paths are relative to the uploads
// folder, with "/" between the parts (that's also how they appear in /Images/uploads/... URLs).
public sealed class ImagePaths(StoragePaths storage, bool useFrameFolder)
{
    public const string Extension = ".jpeg";

    public bool UseFrameFolder => useFrameFolder;

    public string UploadsRoot => storage.UploadsPath;

    // USE_FRAME_NAME_IN_PATH: only "false" (any casing) turns it off; missing or anything that
    // isn't a bool keeps the frame's folder, as before.
    public static bool ParseUseFrameFolder(string? value) => !bool.TryParse(value, out var use) || use;

    public string Folder(string? frameName, string? galleryName) =>
        Join(FileNaming.FolderName(frameName, useFrameFolder), FileNaming.FolderName(galleryName));

    public string File(string? frameName, string? galleryName, string? title, int? fileId) =>
        Join(Folder(frameName, galleryName), FileNaming.FileName(title, fileId) + Extension);

    // The full path on disk, or null when the name would lead outside the uploads folder (a gallery
    // called ".."); nothing is read or written there.
    public string? FullPath(string relative)
    {
        var root = Path.GetFullPath(storage.UploadsPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.Ordinal) ? full : null;
    }

    // The image's URL, each part escaped. MeuralImageUploader escaped only the file name; both
    // forms reach the same file.
    public static string Url(string relative) =>
        "/Images/uploads/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

    private static string Join(params string[] parts) => string.Join('/', parts.Where(p => p.Length > 0));
}
