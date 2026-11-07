namespace MeuralImageBackup.Core;

// Where everything lives. The defaults are MeuralImageUploader's paths relative to the working
// directory (/app in the container): the images in uploads/, the database in database/meural.db.
// Both are Docker volumes, so they must not move (see "Compatibility" in CLAUDE.md). Anything else
// that has to survive a restart goes under the database directory.
public sealed record StoragePaths(string UploadsPath, string DatabaseDirectory, string ThumbnailsPath)
{
    public string DatabaseFile => Path.Combine(DatabaseDirectory, "meural.db");

    // The Data Protection key ring, beside the database so it's in the same volume.
    public string KeysPath => Path.Combine(DatabaseDirectory, "keys");

    public string ConnectionString => $"Data Source={DatabaseFile}";

    // Relative paths are taken from the content root. Thumbnails are only a cache: by default they
    // go to the temp folder, outside the volumes, and are made again after the container is replaced.
    public static StoragePaths From(string? uploads, string? database, string? thumbnails, string root) => new(
        Path.GetFullPath(uploads ?? "uploads", root),
        Path.GetFullPath(database ?? "database", root),
        Path.GetFullPath(thumbnails ?? Path.Combine(Path.GetTempPath(), "meuralimagebackup-thumbs"), root));

    public void CreateDirectories()
    {
        Directory.CreateDirectory(UploadsPath);
        Directory.CreateDirectory(DatabaseDirectory);
        Directory.CreateDirectory(ThumbnailsPath);
    }
}
