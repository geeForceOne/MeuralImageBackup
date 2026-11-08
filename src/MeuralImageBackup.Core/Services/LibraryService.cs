using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeuralImageBackup.Core.Services;

// An image in one gallery, with where its file is and whether it's there. An image that's in two
// galleries is two views (each gallery's folder has its own copy of the file); Id is the image's,
// Key tells the two apart. Times are UTC.
public sealed record ImageView(
    int Id, int FileId, string Name, int GalleryId, string GalleryName, string FrameName,
    DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    string RelativePath, bool Exists, long Size, long Version)
{
    public string Key => $"{GalleryId}-{Id}";
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

// What a sync with the uploads folder changed.
public sealed record SyncResult(int GalleriesAdded, int ImagesAdded, int ImagesLinked, int Skipped)
{
    public bool Changed => GalleriesAdded + ImagesAdded + ImagesLinked > 0;
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class LibraryService(IDbContextFactory<MeuralDb> dbFactory, ImagePaths paths, ILogger<LibraryService> logger)
{
    public async Task<Library> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var links = (await db.GalleryImages.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.ImageId, l => l.GalleryId);
        var files = await Task.Run(() => ScanFiles(ct), ct);

        var byId = galleries.ToDictionary(g => g.Id);
        var views = images
            .SelectMany(i => links[i.Id].Prepend(i.GalleryId).Distinct()
                .Where(byId.ContainsKey)
                .Select(galleryId =>
                {
                    var g = byId[galleryId];
                    var relative = paths.File(g.FrameName, g.Name, i.Name, i.FileId);
                    var found = files.TryGetValue(relative, out var info);
                    return new ImageView(i.Id, i.FileId, i.Name, g.Id, g.Name, g.FrameName,
                        Utc(i.CreatedAt), Utc(i.LastAccessedAt), i.AccessCount,
                        relative, found, found ? info!.Size : 0, found ? info!.Version : 0);
                }))
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
    // folders); then only this gallery's images are deleted. An image that's also in another gallery
    // stays, and belongs to that one from now on.
    public async Task DeleteGalleryAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gallery = await db.Galleries.Include(g => g.Images).FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundException("This gallery was deleted already.");
        var linked = await db.GalleryImages.Where(l => l.GalleryId == id)
            .Join(db.Images, l => l.ImageId, i => i.Id, (l, i) => i).ToListAsync(ct);

        var folder = paths.Folder(gallery.FrameName, gallery.Name);
        var others = await db.Galleries.Where(g => g.Id != id).Select(g => new { g.FrameName, g.Name }).ToListAsync(ct);
        var shared = others.Any(o => paths.Folder(o.FrameName, o.Name) == folder);

        if (!shared && paths.FullPath(folder) is { } full && Directory.Exists(full))
            DeleteQuietly(() => Directory.Delete(full, recursive: true), folder);
        else
            foreach (var image in (gallery.Images ?? []).Concat(linked))
                DeleteFile(paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId));

        // Its own images that are in other galleries too move to one of those; the rest go with it.
        foreach (var image in gallery.Images ?? [])
        {
            var next = await db.GalleryImages.Where(l => l.ImageId == image.Id && l.GalleryId != id)
                .OrderBy(l => l.GalleryId).FirstOrDefaultAsync(ct);
            if (next is not null)
            {
                image.GalleryId = next.GalleryId;
                db.GalleryImages.Remove(next);
            }
        }
        await db.SaveChangesAsync(ct);

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

    // Deletes the image from the gallery: its file in that gallery's folder, and the entry there. In
    // other galleries it stays; when it's in none any more, its entry goes. If the frame shows it
    // again, it's downloaded again.
    public async Task DeleteImageAsync(int id, int galleryId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("This image was deleted already.");
        var gallery = await db.Galleries.FirstOrDefaultAsync(g => g.Id == galleryId, ct)
            ?? throw new NotFoundException("This gallery was deleted already.");
        DeleteFile(paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId));
        await RemoveFromGalleryAsync(db, image, galleryId, ct);
        await db.SaveChangesAsync(ct);
    }

    // Removes the entries of images whose file isn't there (any more), per gallery. Returns how many.
    public async Task<int> RemoveMissingAsync(CancellationToken ct = default)
    {
        var library = await LoadAsync(ct);
        var missing = library.Images.Where(i => !i.Exists).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var removed = 0;
        foreach (var view in missing)
        {
            var image = await db.Images.FirstOrDefaultAsync(i => i.Id == view.Id, ct);
            if (image is null)
                continue;
            await RemoveFromGalleryAsync(db, image, view.GalleryId, ct);
            await db.SaveChangesAsync(ct);
            removed++;
        }
        return removed;
    }

    // The file's full path in the given gallery's folder (or the image's own gallery), for the
    // thumbnail endpoint; null when the image or its file is gone.
    public async Task<string?> FilePathAsync(int imageId, int? galleryId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null)
            return null;
        var inGallery = galleryId is { } g && (g == image.GalleryId ||
            await db.GalleryImages.AnyAsync(l => l.GalleryId == g && l.ImageId == imageId, ct));
        var gallery = await db.Galleries.AsNoTracking().FirstOrDefaultAsync(x => x.Id == (inGallery ? galleryId!.Value : image.GalleryId), ct);
        if (gallery is null)
            return null;
        var full = paths.FullPath(paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId));
        return full is not null && File.Exists(full) ? full : null;
    }

    // Brings the database in line with the uploads folder, without removing anything:
    // - a gallery folder without a gallery gets one (without the frames' folders its frame is "Unknown"),
    // - an image file whose image is recorded in another gallery is linked to this one,
    // - an image file nobody recorded gets an entry (with the Meural id when the file is named after it).
    // Files are matched to images by the file name the image would be saved under. When several
    // recorded images could be the file and none is clearly it, the file is skipped.
    public async Task<SyncResult> SyncWithDiskAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Reading the uploads folder");
        var files = await Task.Run(() => ScanFileDetails(ct), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.ToListAsync(ct);
        var images = await db.Images.ToListAsync(ct);
        var links = await db.GalleryImages.ToListAsync(ct);

        var members = new HashSet<(int Gallery, int Image)>(images.Select(i => (i.GalleryId, i.Id)).Concat(links.Select(l => (l.GalleryId, l.ImageId))));
        var byFolder = galleries.GroupBy(g => paths.Folder(g.FrameName, g.Name), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(g => g.LastAccessedAt).ToList(), StringComparer.Ordinal);
        var byFileName = images.GroupBy(i => FileNaming.FileName(i.Name, i.FileId), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var byFileId = images.Where(i => i.FileId != 0).GroupBy(i => i.FileId).ToDictionary(x => x.Key, x => x.First());
        var homeOf = galleries.ToDictionary(g => g.Id);

        int galleriesAdded = 0, imagesAdded = 0, imagesLinked = 0, skipped = 0;
        var depth = paths.UseFrameFolder ? 3 : 2;

        progress?.Report("Matching files with the database");
        foreach (var folderFiles in files.GroupBy(f => f.Folder, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var parts = folderFiles.Key.Split('/');
            if (folderFiles.Key.Length == 0 || parts.Length != depth - 1)
            {
                skipped += folderFiles.Count();
                continue;
            }

            if (!byFolder.TryGetValue(folderFiles.Key, out var owners))
            {
                var times = folderFiles.Select(f => f.Time).ToList();
                var gallery = new Gallery
                {
                    FrameName = paths.UseFrameFolder ? parts[0] : "Unknown",
                    Name = parts[^1],
                    CreatedAt = times.Min(),
                    LastAccessedAt = times.Max(),
                    AccessCount = 0,
                };
                db.Galleries.Add(gallery);
                await db.SaveChangesAsync(ct);
                homeOf[gallery.Id] = gallery;
                owners = [gallery];
                byFolder[folderFiles.Key] = owners;
                galleriesAdded++;
            }
            var ownerIds = owners.Select(o => o.Id).ToHashSet();
            var target = owners[0];

            foreach (var file in folderFiles)
            {
                var candidates = byFileName.TryGetValue(file.Name, out var named) ? named.ToList() : [];
                if (int.TryParse(file.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                    byFileId.TryGetValue(number, out var byId) && !candidates.Contains(byId))
                    candidates.Add(byId);

                if (candidates.Any(c => ownerIds.Any(o => members.Contains((o, c.Id)))))
                    continue;

                if (candidates.Count == 0)
                {
                    var image = new Image
                    {
                        GalleryId = target.Id,
                        FileId = number,
                        Name = file.Name,
                        CreatedAt = file.Time,
                        LastAccessedAt = file.Time,
                        AccessCount = 0,
                    };
                    db.Images.Add(image);
                    await db.SaveChangesAsync(ct);
                    members.Add((target.Id, image.Id));
                    // The same file in another gallery's folder is this image too.
                    if (named is null)
                        byFileName[file.Name] = [image];
                    else
                        named.Add(image);
                    if (number != 0)
                        byFileId[number] = image;
                    imagesAdded++;
                    continue;
                }

                // Several images with this name: the one on the same frame, if there's just one.
                var match = candidates.Count == 1 ? candidates[0]
                    : candidates.Where(c => homeOf.TryGetValue(c.GalleryId, out var home) && home.FrameName == target.FrameName).ToList() is [var single] ? single
                    : null;
                if (match is null)
                {
                    logger.LogInformation("Sync skipped {File}: {Count} recorded images have that name", file.Relative, candidates.Count);
                    skipped++;
                    continue;
                }
                db.GalleryImages.Add(new GalleryImage { GalleryId = target.Id, ImageId = match.Id });
                members.Add((target.Id, match.Id));
                imagesLinked++;
            }
        }
        await db.SaveChangesAsync(ct);

        var result = new SyncResult(galleriesAdded, imagesAdded, imagesLinked, skipped);
        logger.LogInformation("Synced with the uploads folder: {Galleries} galleries added, {Images} images added, {Linked} linked, {Skipped} files skipped",
            galleriesAdded, imagesAdded, imagesLinked, skipped);
        return result;
    }

    // An image leaves a gallery: a link goes; leaving its own gallery, it moves to another one it's in,
    // or its entry goes when there's none.
    private static async Task RemoveFromGalleryAsync(MeuralDb db, Image image, int galleryId, CancellationToken ct)
    {
        if (image.GalleryId != galleryId)
        {
            await db.GalleryImages.Where(l => l.GalleryId == galleryId && l.ImageId == image.Id).ExecuteDeleteAsync(ct);
            return;
        }
        var next = await db.GalleryImages.Where(l => l.ImageId == image.Id).OrderBy(l => l.GalleryId).FirstOrDefaultAsync(ct);
        if (next is null)
        {
            db.Images.Remove(image);
            return;
        }
        image.GalleryId = next.GalleryId;
        db.GalleryImages.Remove(next);
    }

    private sealed record FileInfoLite(long Size, long Version);

    // An image file for the sync: its folder ("frame/gallery"), name without ".jpeg" and time (UTC).
    private sealed record DiskFile(string Relative, string Folder, string Name, DateTime Time);

    // Every file under uploads/, by its relative path with "/". Linux names are case-sensitive, so
    // the lookup is too.
    private Dictionary<string, FileInfoLite> ScanFiles(CancellationToken ct) =>
        EnumerateUploads(ct).ToDictionary(f => f.Relative, f => new FileInfoLite(f.Info.Length, f.Info.LastWriteTimeUtc.Ticks), StringComparer.Ordinal);

    private List<DiskFile> ScanFileDetails(CancellationToken ct) =>
        EnumerateUploads(ct)
            .Where(f => f.Relative.EndsWith(ImagePaths.Extension, StringComparison.Ordinal) && f.Info.Length > 0)
            .Select(f =>
            {
                var slash = f.Relative.LastIndexOf('/');
                var folder = slash < 0 ? "" : f.Relative[..slash];
                var name = f.Relative[(slash + 1)..^ImagePaths.Extension.Length];
                return new DiskFile(f.Relative, folder, name, f.Info.LastWriteTimeUtc);
            })
            .ToList();

    private IEnumerable<(string Relative, FileInfo Info)> EnumerateUploads(CancellationToken ct)
    {
        var root = new DirectoryInfo(paths.UploadsRoot);
        if (!root.Exists)
            yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var file in root.EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            yield return (Path.GetRelativePath(root.FullName, file.FullName).Replace(Path.DirectorySeparatorChar, '/'), file);
        }
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
