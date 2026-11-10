using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeuralImageBackup.Core.Services;

// An image: one Meural file shown in one gallery, with where its file is and whether it's there.
// The same Meural file shown in two galleries is two images, each with its own file in its own
// gallery's folder (in pre-release builds one image could be listed in several
// galleries, see LegacyCleanup). Times are UTC.
public sealed record ImageView(
    int Id, int FileId, string Name, int GalleryId, string GalleryName, string FrameName,
    DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    string RelativePath, bool Exists, long Size, long Version)
{
    public int Key => Id;
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
// Legacy is what the one-time cleanup of images listed in several galleries would do (null when
// there's nothing to clean up).
public sealed record Library(IReadOnlyList<GalleryView> Galleries, IReadOnlyList<ImageView> Images, long DiskSize, LegacyCleanup? Legacy = null)
{
    public IReadOnlyList<string> Frames { get; } = Galleries.Select(g => g.FrameName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();
}

// In pre-release builds an image could be listed in several galleries (the table GalleryImages), with
// a copy of its file in each of their folders. The cleanup makes each such image belong to one
// gallery again: its own (where it was first shown) when that folder has its file, else the first
// other one that has it, where it moves. The copies in the other galleries' folders are deleted and
// those galleries no longer list it (the user's decision). A file another image uses is never
// deleted. Images counts the images listed in several galleries, Files and Bytes the copies deleted.
public sealed record LegacyCleanup(int Images, int Files, long Bytes);

// What removing identical files or the legacy cleanup did.
public sealed record DedupeResult(int Images, int FilesDeleted, long BytesFreed);

// What a sync with the uploads folder changed. Skipped counts the image files outside a gallery
// folder, which it can't place.
public sealed record SyncResult(int GalleriesAdded, int ImagesAdded, int Skipped)
{
    public bool Changed => GalleriesAdded + ImagesAdded > 0;
}

// Why a file in the uploads folder is loose (see LibraryService.FindLooseFilesAsync).
public enum LooseReason { OutsideGallery, NotAnImage }

// A file no image uses.
public sealed record LooseFile(string RelativePath, LooseReason Reason, long Size, DateTime Time)
{
    public bool IsImage => Reason != LooseReason.NotAnImage;
    public string Url => ImagePaths.Url(RelativePath);
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class LibraryService(IDbContextFactory<MeuralDb> dbFactory, ImagePaths paths, ILogger<LibraryService> logger)
{
    public async Task<Library> LoadAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var links = await db.GalleryImages.AsNoTracking().ToListAsync(ct);
        var files = await Task.Run(() => ScanFiles(ct), ct);

        var byId = galleries.ToDictionary(g => g.Id);
        var views = images
            .Where(i => byId.ContainsKey(i.GalleryId))
            .Select(i =>
            {
                var g = byId[i.GalleryId];
                var relative = paths.File(g.FrameName, g.Name, i.Name, i.FileId);
                var info = files.GetValueOrDefault(relative);
                return new ImageView(i.Id, i.FileId, i.Name, g.Id, g.Name, g.FrameName,
                    Utc(i.CreatedAt), Utc(i.LastAccessedAt), i.AccessCount,
                    relative, info is not null, info?.Size ?? 0, info?.Version ?? 0);
            })
            .ToList();

        var perGallery = views.ToLookup(v => v.GalleryId);
        var galleryViews = galleries
            .Select(g => new GalleryView(g.Id, g.Name, g.FrameName, Utc(g.CreatedAt), Utc(g.LastAccessedAt), g.AccessCount,
                perGallery[g.Id].OrderByDescending(i => i.LastAccessedAt).ToList()))
            .ToList();

        LegacyCleanup? legacy = null;
        if (links.Count > 0)
        {
            var plan = PlanLegacy(galleries, images, links, r => files.TryGetValue(r, out var f) ? f.Size : null);
            if (plan.Count > 0)
                legacy = new LegacyCleanup(plan.Count, plan.Sum(p => p.Delete.Count), plan.Sum(p => p.Delete.Sum(d => d.Size)));
        }

        return new Library(galleryViews, views, files.Values.Sum(f => f.Size), legacy);
    }

    // Deletes the gallery's images and their files. Its folder goes as a whole (as before), unless
    // another gallery uses the same folder (the same gallery name on two frames without the frames'
    // folders); then only the files no image of those galleries uses are deleted.
    public async Task DeleteGalleryAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var gallery = await db.Galleries.Include(g => g.Images).FirstOrDefaultAsync(g => g.Id == id, ct)
            ?? throw new NotFoundException("This gallery was deleted already.");

        var folder = paths.Folder(gallery.FrameName, gallery.Name);
        var others = await db.Galleries.AsNoTracking().Where(g => g.Id != id).ToListAsync(ct);
        var sharing = others.Where(o => paths.Folder(o.FrameName, o.Name) == folder).Select(o => o.Id).ToList();

        if (sharing.Count == 0)
        {
            if (paths.FullPath(folder) is { } full && Directory.Exists(full))
                DeleteQuietly(() => Directory.Delete(full, recursive: true), folder);
        }
        else
        {
            var used = (await db.Images.AsNoTracking().Where(i => sharing.Contains(i.GalleryId)).ToListAsync(ct))
                .Select(i => paths.File(gallery.FrameName, gallery.Name, i.Name, i.FileId)).ToHashSet(StringComparer.Ordinal);
            foreach (var image in gallery.Images ?? [])
                if (paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId) is var file && !used.Contains(file))
                    DeleteFile(file);
        }

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

    // Deletes the image: its file and its entry. The same Meural file in other galleries is other
    // images, which stay. If the frame shows it again, it's downloaded again.
    public async Task DeleteImageAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("This image was deleted already.");
        var file = paths.File(image.Gallery!.FrameName, image.Gallery.Name, image.Name, image.FileId);
        if (!await UsedByOthersAsync(db, file, image.Id, ct))
            DeleteFile(file);
        db.Images.Remove(image);
        await db.SaveChangesAsync(ct);
    }

    // Removes an image whose file is identical to the kept image's: its file and its entry. The kept
    // image stays as it is. A file another image uses too (same title in the same folder) stays. If a
    // frame shows the removed image again, it's recorded and downloaded anew. Returns how many files
    // were deleted and their size.
    public async Task<(int Files, long Bytes)> RemoveIdenticalAsync(int keepId, int removeId, CancellationToken ct = default)
    {
        if (keepId == removeId)
            throw new InvalidOperationException("The image to keep can't be removed.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keep = await db.Images.AsNoTracking().Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == keepId, ct)
            ?? throw new NotFoundException("The image to keep was deleted meanwhile.");
        var remove = await db.Images.Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == removeId, ct)
            ?? throw new NotFoundException("This image was removed already.");

        var kept = paths.File(keep.Gallery!.FrameName, keep.Gallery.Name, keep.Name, keep.FileId);
        if (paths.FullPath(kept) is not { } keptFull || !File.Exists(keptFull))
            throw new InvalidOperationException($"The file of {keep.Name} isn't there any more, so nothing was removed.");

        var files = 0;
        var bytes = 0L;
        var relative = paths.File(remove.Gallery!.FrameName, remove.Gallery.Name, remove.Name, remove.FileId);
        if (relative != kept && !await UsedByOthersAsync(db, relative, remove.Id, ct) &&
            paths.FullPath(relative) is { } full && File.Exists(full))
        {
            var size = new FileInfo(full).Length;
            if (DeleteQuietly(() => File.Delete(full), relative))
            {
                files++;
                bytes += size;
            }
        }

        db.Images.Remove(remove);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Removed {Removed} (Meural id {RemovedId}) from {Gallery}: identical to {Kept} (Meural id {KeptId}), {Count} files deleted",
            remove.Name, remove.FileId, remove.Gallery.Name, keep.Name, keep.FileId, files);
        return (files, bytes);
    }

    // Keeps the first image (the most shown) of every group of identical files and removes the others.
    public async Task<DedupeResult> RemoveIdenticalAsync(IReadOnlyList<IdenticalFiles> groups, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int images = 0, files = 0;
        var bytes = 0L;
        for (var n = 0; n < groups.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var group = groups[n];
            progress?.Report($"{n + 1:N0} of {groups.Count:N0}: {group.Keeper.Name}");
            foreach (var other in group.Images.Skip(1))
            {
                try
                {
                    var (f, b) = await RemoveIdenticalAsync(group.Keeper.Id, other.Id, ct);
                    images++;
                    files += f;
                    bytes += b;
                }
                catch (Exception ex) when (ex is NotFoundException or InvalidOperationException)
                {
                    logger.LogInformation("Didn't remove {Image}: {Reason}", other.Name, ex.Message);
                }
            }
        }
        return new DedupeResult(images, files, bytes);
    }

    // The one-time cleanup of images listed in several galleries (see LegacyCleanup), planned again
    // against the database and the disk as they are now. Each image is done on its own, so a stop
    // leaves the done ones done and the rest as they were.
    public async Task<DedupeResult> CleanUpLegacyAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var links = await db.GalleryImages.AsNoTracking().ToListAsync(ct);
        var plan = await Task.Run(() => PlanLegacy(galleries, images, links,
            r => paths.FullPath(r) is { } full && File.Exists(full) ? new FileInfo(full).Length : null), ct);

        int done = 0, files = 0;
        var bytes = 0L;
        foreach (var step in plan)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"{done + 1:N0} of {plan.Count:N0}: {step.Name}");
            foreach (var (relative, size) in step.Delete)
                if (paths.FullPath(relative) is { } full && File.Exists(full) && DeleteQuietly(() => File.Delete(full), relative))
                {
                    files++;
                    bytes += size;
                    logger.LogInformation("Deleted {File}: an extra copy of {Image}", relative, step.Name);
                }

            await db.GalleryImages.Where(l => l.ImageId == step.ImageId).ExecuteDeleteAsync(ct);
            if (step.Remove)
                await db.Images.Where(i => i.Id == step.ImageId).ExecuteDeleteAsync(ct);
            else if (step.MoveTo is { } target)
                await db.Images.Where(i => i.Id == step.ImageId).ExecuteUpdateAsync(u => u.SetProperty(i => i.GalleryId, target), ct);
            done++;
        }
        // What's left links images or galleries that are gone.
        await db.GalleryImages.ExecuteDeleteAsync(ct);
        logger.LogInformation("Cleaned up {Images} images listed in several galleries: {Files} copies deleted", done, files);
        return new DedupeResult(done, files, bytes);
    }

    // One image's part of the legacy cleanup: the copies to delete, and where the image goes (MoveTo
    // when its own folder lacks the file, Remove when another image uses the file it would keep).
    private sealed record LegacyStep(int ImageId, string Name, int? MoveTo, bool Remove, List<(string Relative, long Size)> Delete);

    private List<LegacyStep> PlanLegacy(List<Gallery> galleries, List<Image> images, List<GalleryImage> links, Func<string, long?> sizeOf)
    {
        var byId = galleries.ToDictionary(g => g.Id);
        var linked = links.Where(l => byId.ContainsKey(l.GalleryId)).ToLookup(l => l.ImageId, l => l.GalleryId);
        string PathOf(Image i, Gallery g) => paths.File(g.FrameName, g.Name, i.Name, i.FileId);

        // Every image's own file: never deleted, and no image moves onto another's file.
        var owners = images.Where(i => byId.ContainsKey(i.GalleryId))
            .GroupBy(i => PathOf(i, byId[i.GalleryId]), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(i => i.Id).ToHashSet(), StringComparer.Ordinal);

        var plan = new List<LegacyStep>();
        foreach (var image in images.Where(i => linked.Contains(i.Id) && byId.ContainsKey(i.GalleryId)).OrderBy(i => i.Id))
        {
            var ownPath = PathOf(image, byId[image.GalleryId]);
            var copies = linked[image.Id].Where(id => id != image.GalleryId).Distinct().Order()
                .Select(id => (GalleryId: id, Relative: PathOf(image, byId[id])))
                .Select(c => (c.GalleryId, c.Relative, Size: sizeOf(c.Relative)))
                .ToList();

            int? moveTo = null;
            var remove = false;
            var keep = ownPath;
            if (sizeOf(ownPath) is null)
            {
                var first = copies.FindIndex(c => c.Size is not null);
                if (first >= 0)
                {
                    keep = copies[first].Relative;
                    if (owners.TryGetValue(keep, out var users) && users.Any(u => u != image.Id))
                        remove = true;
                    else
                        moveTo = copies[first].GalleryId;
                }
            }
            var delete = copies
                .Where(c => c.Size is not null && c.Relative != keep && c.Relative != ownPath && !owners.ContainsKey(c.Relative))
                .DistinctBy(c => c.Relative, StringComparer.Ordinal)
                .Select(c => (c.Relative, c.Size!.Value))
                .ToList();
            plan.Add(new LegacyStep(image.Id, image.Name, moveTo, remove, delete));
        }
        return plan;
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

    // The image's file, full path, for the thumbnails and the photo source API; null when the image
    // or its file is gone.
    public async Task<string?> FilePathAsync(int imageId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.AsNoTracking().Include(i => i.Gallery).FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image?.Gallery is not { } g)
            return null;
        var full = paths.FullPath(paths.File(g.FrameName, g.Name, image.Name, image.FileId));
        return full is not null && File.Exists(full) ? full : null;
    }

    // Brings the database in line with the uploads folder, without removing anything:
    // - a gallery folder without a gallery gets one (without the frames' folders its frame is "Unknown"),
    // - an image file that no image of its folder's gallery uses gets an entry in that gallery: a file
    //   belongs to the gallery whose folder it's in. When the file is named like images recorded
    //   elsewhere that all have one Meural id (the same Meural file shown in another gallery), the
    //   entry takes that id; else the id from a numeric name, or 0, which registration fills in.
    // The copies of images listed in several galleries (pre-release builds) are left to the legacy cleanup.
    public Task<SyncResult> SyncWithDiskAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        SyncAsync(apply: true, loose: null, progress, ct);

    // The files in the uploads folder that no image uses and the sync can't place: image files
    // outside a gallery folder, and files that aren't images (other types, empty files, unfinished
    // downloads). Reads only.
    public async Task<IReadOnlyList<LooseFile>> FindLooseFilesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var loose = new List<LooseFile>();
        await SyncAsync(apply: false, loose, progress, ct);
        var others = await Task.Run(() => EnumerateUploads(ct)
            .Where(f => !f.Relative.EndsWith(ImagePaths.Extension, StringComparison.Ordinal) || f.Info.Length == 0)
            .Select(f => new LooseFile(f.Relative, LooseReason.NotAnImage, f.Info.Length, f.Info.LastWriteTimeUtc))
            .ToList(), ct);
        return loose.Concat(others).OrderBy(f => f.Reason).ThenBy(f => f.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // Deletes loose files: only those FindLooseFilesAsync lists right now, so a file an image uses is
    // never deleted, whatever is asked. Folders left empty go too. Returns how many and their size.
    public async Task<(int Files, long Bytes)> DeleteLooseFilesAsync(IEnumerable<string> relativePaths, CancellationToken ct = default)
    {
        var loose = (await FindLooseFilesAsync(null, ct)).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var files = 0;
        var bytes = 0L;
        foreach (var relative in relativePaths.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!loose.TryGetValue(relative, out var file) || paths.FullPath(relative) is not { } full || !File.Exists(full))
                continue;
            if (!DeleteQuietly(() => File.Delete(full), relative))
                continue;
            files++;
            bytes += file.Size;
            logger.LogInformation("Deleted the loose file {File} ({Reason})", relative, file.Reason);
            DeleteEmptyFolders(Path.GetDirectoryName(full));
        }
        return (files, bytes);
    }

    // The sync's work. With apply false nothing is written: new galleries get temporary ids, so the
    // matching runs exactly as it would, and the skipped files are collected in loose.
    private async Task<SyncResult> SyncAsync(bool apply, List<LooseFile>? loose, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reading the uploads folder");
        var files = await Task.Run(() => ScanFileDetails(progress, ct), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var links = await db.GalleryImages.AsNoTracking().ToListAsync(ct);

        // The file names each gallery's images use, the legacy links' included.
        var imageById = images.ToDictionary(i => i.Id);
        var used = images.Select(i => (i.GalleryId, FileNaming.FileName(i.Name, i.FileId)))
            .Concat(links.Where(l => imageById.ContainsKey(l.ImageId))
                .Select(l => (l.GalleryId, FileNaming.FileName(imageById[l.ImageId].Name, imageById[l.ImageId].FileId))))
            .ToHashSet();
        var byFolder = galleries.GroupBy(g => paths.Folder(g.FrameName, g.Name), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(g => g.LastAccessedAt).ToList(), StringComparer.Ordinal);
        var idsByFileName = images.Where(i => i.FileId != 0).GroupBy(i => FileNaming.FileName(i.Name, i.FileId), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(i => i.FileId).Distinct().ToList(), StringComparer.Ordinal);

        var tempId = -1;
        int galleriesAdded = 0, imagesAdded = 0, outside = 0;
        var depth = paths.UseFrameFolder ? 3 : 2;

        var folders = files.GroupBy(f => f.Folder, StringComparer.Ordinal).ToList();
        var done = 0;
        foreach (var folderFiles in folders)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Checking folder {++done:N0} of {folders.Count:N0}: {folderFiles.Key}");
            var parts = folderFiles.Key.Split('/');
            if (folderFiles.Key.Length == 0 || parts.Length != depth - 1)
            {
                foreach (var file in folderFiles)
                {
                    outside++;
                    loose?.Add(new LooseFile(file.Relative, LooseReason.OutsideGallery, file.Size, file.Time));
                    if (apply)
                        logger.LogInformation("Sync skipped {File}: not in a gallery folder", file.Relative);
                }
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
                if (apply)
                {
                    db.Galleries.Add(gallery);
                    await db.SaveChangesAsync(ct);
                }
                else
                {
                    gallery.Id = tempId--;
                }
                owners = [gallery];
                byFolder[folderFiles.Key] = owners;
                galleriesAdded++;
            }
            var target = owners[0];

            foreach (var file in folderFiles)
            {
                if (owners.Any(o => used.Contains((o.Id, file.Name))))
                    continue;
                var fileId = idsByFileName.TryGetValue(file.Name, out var ids) && ids is [var known] ? known
                    : int.TryParse(file.Name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number
                    : 0;
                if (apply)
                    db.Images.Add(new Image
                    {
                        GalleryId = target.Id,
                        FileId = fileId,
                        Name = file.Name,
                        CreatedAt = file.Time,
                        LastAccessedAt = file.Time,
                        AccessCount = 0,
                    });
                used.Add((target.Id, file.Name));
                imagesAdded++;
            }
        }
        var result = new SyncResult(galleriesAdded, imagesAdded, outside);
        if (!apply)
            return result;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Synced with the uploads folder: {Galleries} galleries added, {Images} images added, {Outside} files outside gallery folders skipped",
            galleriesAdded, imagesAdded, outside);
        return result;
    }

    // Whether another image uses the same file (the same title in a folder two galleries share).
    private async Task<bool> UsedByOthersAsync(MeuralDb db, string relative, int imageId, CancellationToken ct)
    {
        var slash = relative.LastIndexOf('/');
        var folder = slash < 0 ? "" : relative[..slash];
        var name = relative[(slash + 1)..^ImagePaths.Extension.Length];
        var galleries = (await db.Galleries.AsNoTracking().ToListAsync(ct))
            .Where(g => paths.Folder(g.FrameName, g.Name) == folder).Select(g => g.Id).ToList();
        return (await db.Images.AsNoTracking().Where(i => i.Id != imageId && galleries.Contains(i.GalleryId)).ToListAsync(ct))
            .Any(i => FileNaming.FileName(i.Name, i.FileId) == name);
    }

    // Removes empty folders from the given one up, never the uploads folder itself.
    private void DeleteEmptyFolders(string? folder)
    {
        var root = Path.GetFullPath(paths.UploadsRoot).TrimEnd(Path.DirectorySeparatorChar);
        while (folder is not null)
        {
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (full.Length <= root.Length || !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
                return;
            if (!DeleteQuietly(() => Directory.Delete(full), full))
                return;
            folder = Path.GetDirectoryName(full);
        }
    }

    private sealed record FileInfoLite(long Size, long Version);

    // An image file for the sync: its folder ("frame/gallery"), name without ".jpeg" and time (UTC).
    private sealed record DiskFile(string Relative, string Folder, string Name, DateTime Time, long Size);

    // Every file under uploads/, by its relative path with "/". Linux names are case-sensitive, so
    // the lookup is too.
    private Dictionary<string, FileInfoLite> ScanFiles(CancellationToken ct) =>
        EnumerateUploads(ct).ToDictionary(f => f.Relative, f => new FileInfoLite(f.Info.Length, f.Info.LastWriteTimeUtc.Ticks), StringComparer.Ordinal);

    private List<DiskFile> ScanFileDetails(IProgress<string>? progress, CancellationToken ct)
    {
        var found = new List<DiskFile>();
        var seen = 0;
        foreach (var f in EnumerateUploads(ct))
        {
            if (++seen % 250 == 0)
                progress?.Report($"Reading the uploads folder: {seen:N0} files");
            if (!f.Relative.EndsWith(ImagePaths.Extension, StringComparison.Ordinal) || f.Info.Length == 0)
                continue;
            var slash = f.Relative.LastIndexOf('/');
            var folder = slash < 0 ? "" : f.Relative[..slash];
            var name = f.Relative[(slash + 1)..^ImagePaths.Extension.Length];
            found.Add(new DiskFile(f.Relative, folder, name, f.Info.LastWriteTimeUtc, f.Info.Length));
        }
        return found;
    }

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

    private bool DeleteQuietly(Action delete, string what)
    {
        try
        {
            delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't delete {Path}: {Reason}", what, ex.Message);
            return false;
        }
    }

    // SQLite gives the stored times back without a kind; they were written as UTC.
    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
