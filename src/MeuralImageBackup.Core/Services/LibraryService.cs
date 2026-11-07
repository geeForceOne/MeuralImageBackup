using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeuralImageBackup.Core.Services;

// An image with where its file is and whether it's there. Times are UTC.
public sealed record ImageView(
    int Id, int FileId, string Name, int GalleryId, string GalleryName, string FrameName,
    DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    string RelativePath, bool Exists, long Size, long Version)
{
    public string Url => ImagePaths.Url(RelativePath);
}

public sealed record GalleryView(
    int Id, string Name, string FrameName, DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    IReadOnlyList<ImageView> Images)
{
    public long Size => Images.Where(i => i.Exists).Sum(i => i.Size);
    public int Missing => Images.Count(i => !i.Exists);
}

// Everything recorded, read at once: the collection is small enough (thousands of images) to
// filter and sort in memory, and one walk over the uploads folder answers which files exist.
public sealed record Library(IReadOnlyList<GalleryView> Galleries, IReadOnlyList<ImageView> Images, long DiskSize)
{
    public IReadOnlyList<string> Frames { get; } = Galleries.Select(g => g.FrameName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class LibraryService(IDbContextFactory<MeuralDb> dbFactory, ImagePaths paths, ILogger<LibraryService> logger)
{
    public async Task<Library> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var files = await Task.Run(() => ScanFiles(ct), ct);

        var byId = galleries.ToDictionary(g => g.Id);
        var views = images
            .Where(i => byId.ContainsKey(i.GalleryId))
            .Select(i =>
            {
                var g = byId[i.GalleryId];
                var relative = paths.File(g.FrameName, g.Name, i.Name, i.FileId);
                var found = files.TryGetValue(relative, out var info);
                return new ImageView(i.Id, i.FileId, i.Name, g.Id, g.Name, g.FrameName,
                    Utc(i.CreatedAt), Utc(i.LastAccessedAt), i.AccessCount,
                    relative, found, found ? info!.Size : 0, found ? info!.Version : 0);
            })
            .ToList();

        var perGallery = views.ToLookup(v => v.GalleryId);
        var galleryViews = galleries
            .Select(g => new GalleryView(g.Id, g.Name, g.FrameName, Utc(g.CreatedAt), Utc(g.LastAccessedAt), g.AccessCount,
                perGallery[g.Id].OrderByDescending(i => i.LastAccessedAt).ToList()))
            .ToList();

        return new Library(galleryViews, views, files.Values.Sum(f => f.Size));
    }

    // Deletes the gallery's entries and its files. Its folder goes as a whole (as before), unless
    // another gallery uses the same folder (the same gallery name on two frames without the frames'
    // folders); then only this gallery's images are deleted.
    public async Task DeleteGalleryAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gallery = await db.Galleries.Include(g => g.Images).FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundException("This gallery was deleted already.");

        var folder = paths.Folder(gallery.FrameName, gallery.Name);
        var others = await db.Galleries.Where(g => g.Id != id).Select(g => new { g.FrameName, g.Name }).ToListAsync(ct);
        var shared = others.Any(o => paths.Folder(o.FrameName, o.Name) == folder);

        if (!shared && paths.FullPath(folder) is { } full && Directory.Exists(full))
            DeleteQuietly(() => Directory.Delete(full, recursive: true), folder);
        else
            foreach (var image in gallery.Images ?? [])
                DeleteFile(paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId));

        db.Galleries.Remove(gallery);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Deleted the gallery {Gallery} of {Frame}", gallery.Name, gallery.FrameName);
    }

    public async Task<int> DeleteGalleriesAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeleteGalleryAsync(id, ct);
                count++;
            }
            catch (NotFoundException)
            {
            }
        }
        return count;
    }

    // Deletes the image's file and its entry. If the frame shows it again, it's downloaded again.
    public async Task DeleteImageAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("This image was deleted already.");
        DeleteFile(paths.File(image.Gallery!.FrameName, image.Gallery.Name, image.Name, image.FileId));
        db.Images.Remove(image);
        await db.SaveChangesAsync(ct);
    }

    // Removes the entries of images whose file isn't there (any more). Returns how many.
    public async Task<int> RemoveMissingAsync(CancellationToken ct = default)
    {
        var library = await LoadAsync(ct);
        var missing = library.Images.Where(i => !i.Exists).Select(i => i.Id).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = 0;
        foreach (var chunk in missing.Chunk(500))
            removed += await db.Images.Where(i => chunk.Contains(i.Id)).ExecuteDeleteAsync(ct);
        return removed;
    }

    // The file's full path, for the thumbnail endpoint; null when the image or its file is gone.
    public async Task<string?> FilePathAsync(int imageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.AsNoTracking().Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image?.Gallery is null)
            return null;
        var full = paths.FullPath(paths.File(image.Gallery.FrameName, image.Gallery.Name, image.Name, image.FileId));
        return full is not null && File.Exists(full) ? full : null;
    }

    private sealed record FileInfoLite(long Size, long Version);

    // Every file under uploads/, by its relative path with "/". Linux names are case-sensitive, so
    // the lookup is too.
    private Dictionary<string, FileInfoLite> ScanFiles(CancellationToken ct)
    {
        var result = new Dictionary<string, FileInfoLite>(StringComparer.Ordinal);
        var root = new DirectoryInfo(paths.UploadsRoot);
        if (!root.Exists)
            return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in root.EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/');
            result[relative] = new FileInfoLite(file.Length, file.LastWriteTimeUtc.Ticks);
        }
        return result;
    }

    private void DeleteFile(string relative)
    {
        if (paths.FullPath(relative) is { } full && File.Exists(full))
            DeleteQuietly(() => File.Delete(full), relative);
    }

    private void DeleteQuietly(Action delete, string what)
    {
        try { delete(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete {Path}: {Reason}", what, ex.Message);
        }
    }

    // SQLite gives the stored times back without a kind; they were written as UTC.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
