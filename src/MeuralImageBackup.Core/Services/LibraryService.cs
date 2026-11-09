using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeuralImageBackup.Core.Services;

// An image in one gallery, with where its file is and whether it's there. An image that's in two
// galleries is two views; Id is the image's, Key tells the two apart. Times are UTC.
// Usually each gallery's folder has its own copy of the file. When it doesn't but another of the
// image's galleries does (a duplicate copy was deleted, or the download was skipped because the
// image was stored already), the view shows that copy: RelativePath is the file shown, and
// StoredInGalleryId/StoredInGalleryName say whose folder it's in.
public sealed record ImageView(
    int Id, int FileId, string Name, int GalleryId, string GalleryName, string FrameName,
    DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    string RelativePath, bool Exists, long Size, long Version,
    int? StoredInGalleryId = null, string? StoredInGalleryName = null, int HomeGalleryId = 0)
{
    public string Key => $"{GalleryId}-{Id}";
    public string Url => ImagePaths.Url(RelativePath);

    // Whether this gallery's own folder has the file.
    public bool HasOwnCopy => Exists && StoredInGalleryId is null;

    // Whether this is the gallery the image was first shown in (Images.GalleryId).
    public bool IsHome => GalleryId == HomeGalleryId;
}

public sealed record GalleryView(
    int Id, string Name, string FrameName, DateTime CreatedAt, DateTime LastAccessedAt, int AccessCount,
    IReadOnlyList<ImageView> Images)
{
    // What its own folder holds; a copy shown from another gallery's folder counts there.
    public long Size => Images.Where(i => i.HasOwnCopy).Sum(i => i.Size);
    public int Missing => Images.Count(i => !i.Exists);
}

// Everything recorded, read at once: the collection is small enough (thousands of images) to
// filter and sort in memory, and one walk over the uploads folder answers which files exist.
public sealed record Library(IReadOnlyList<GalleryView> Galleries, IReadOnlyList<ImageView> Images, long DiskSize)
{
    public IReadOnlyList<string> Frames { get; } = Galleries.Select(g => g.FrameName).Distinct().Order(StringComparer.CurrentCultureIgnoreCase).ToList();

    // The images whose file is in more than one gallery's folder, most wasted space first.
    public IReadOnlyList<Duplicate> Duplicates { get; } = Images
        .GroupBy(i => i.Id)
        .Where(g => g.Count(v => v.HasOwnCopy) > 1)
        .Select(g => new Duplicate(g.OrderByDescending(v => v.HasOwnCopy).ThenBy(v => v.GalleryName, StringComparer.CurrentCultureIgnoreCase).ToList()))
        .OrderByDescending(d => d.Wasted)
        .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

// One image with copies of its file in several gallery folders. Views lists it in every gallery it's
// in, those with their own copy first.
public sealed record Duplicate(IReadOnlyList<ImageView> Views)
{
    public int Id => Views[0].Id;
    public string Name => Views[0].Name;
    public IEnumerable<ImageView> Copies => Views.Where(v => v.HasOwnCopy);

    // The copy "keep one of each" keeps: the one in the gallery the image was first shown in, when
    // its folder has it, else the first.
    public ImageView Keeper => Copies.FirstOrDefault(c => c.IsHome) ?? Copies.First();

    // The space the other copies take.
    public long Wasted => Copies.Where(c => c != Keeper).Sum(c => c.Size);
}

// What removing duplicate copies did.
public sealed record DedupeResult(int Images, int FilesDeleted, long BytesFreed);

// What a sync with the uploads folder changed. Skipped counts the image files it couldn't place:
// OutsideGallery of them aren't in a gallery folder, the rest have a name several images share.
public sealed record SyncResult(int GalleriesAdded, int ImagesAdded, int ImagesLinked, int Skipped, int OutsideGallery = 0)
{
    public bool Changed => GalleriesAdded + ImagesAdded + ImagesLinked > 0;
    public int SharedName => Skipped - OutsideGallery;
}

// Why a file in the uploads folder is loose (see LibraryService.FindLooseFilesAsync).
public enum LooseReason { OutsideGallery, SharedName, NotAnImage }

// A file no image uses. Candidates names the images whose file name it has (SharedName only).
public sealed record LooseFile(string RelativePath, LooseReason Reason, long Size, DateTime Time, IReadOnlyList<string> Candidates)
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
        var links = (await db.GalleryImages.AsNoTracking().ToListAsync(ct)).ToLookup(l => l.ImageId, l => l.GalleryId);
        var files = await Task.Run(() => ScanFiles(ct), ct);

        var byId = galleries.ToDictionary(g => g.Id);
        var views = images
            .SelectMany(i =>
            {
                var copies = links[i.Id].Prepend(i.GalleryId).Distinct()
                    .Where(byId.ContainsKey)
                    .Select(id =>
                    {
                        var relative = paths.File(byId[id].FrameName, byId[id].Name, i.Name, i.FileId);
                        return (Gallery: byId[id], Relative: relative, Info: files.GetValueOrDefault(relative));
                    })
                    .ToList();
                // A gallery whose folder has no copy shows the first copy another gallery has.
                var stored = copies.FirstOrDefault(c => c.Info is not null);
                return copies.Select(c =>
                {
                    var shared = c.Info is null && stored.Info is not null;
                    var (relative, info) = shared ? (stored.Relative, stored.Info) : (c.Relative, c.Info);
                    return new ImageView(i.Id, i.FileId, i.Name, c.Gallery.Id, c.Gallery.Name, c.Gallery.FrameName,
                        Utc(i.CreatedAt), Utc(i.LastAccessedAt), i.AccessCount,
                        relative, info is not null, info?.Size ?? 0, info?.Version ?? 0,
                        shared ? stored.Gallery.Id : null, shared ? stored.Gallery.Name : null, i.GalleryId);
                });
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

        // An image that stays in other galleries keeps its file: when this folder has the only copy,
        // the copy moves to the gallery it belongs to from now on.
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in (gallery.Images ?? []).Concat(linked))
            if (await KeepLastCopyAsync(db, image, gallery, ct) is { } stays)
                keep.Add(stays);

        if (!shared && paths.FullPath(folder) is { } full && Directory.Exists(full))
            DeleteQuietly(() => Directory.Delete(full, recursive: true), folder);
        else
            foreach (var image in (gallery.Images ?? []).Concat(linked))
                if (paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId) is var file && !keep.Contains(file))
                    DeleteFile(file);

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
    // other galleries it stays, with its file (the last copy moves to one of them rather than being
    // deleted); when it's in none any more, its entry goes. If the frame shows it again, it's
    // downloaded again.
    public async Task DeleteImageAsync(int id, int galleryId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.FirstOrDefaultAsync(i => i.Id == id, ct)
            ?? throw new NotFoundException("This image was deleted already.");
        var gallery = await db.Galleries.FirstOrDefaultAsync(g => g.Id == galleryId, ct)
            ?? throw new NotFoundException("This gallery was deleted already.");
        if (await KeepLastCopyAsync(db, image, gallery, ct) is null)
            DeleteFile(paths.File(gallery.FrameName, gallery.Name, image.Name, image.FileId));
        await RemoveFromGalleryAsync(db, image, galleryId, ct);
        await db.SaveChangesAsync(ct);
    }

    // Deletes the image's duplicate files: every copy except the one in keepGalleryId's folder. The
    // image stays listed in all its galleries, which then show the copy that's left. Returns how
    // many files were deleted and their size.
    public async Task<(int Files, long Bytes)> KeepOneCopyAsync(int imageId, int keepGalleryId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == imageId, ct)
            ?? throw new NotFoundException("This image was deleted already.");
        var galleries = await GalleriesOfAsync(db, image, ct);
        var keep = galleries.FirstOrDefault(g => g.Id == keepGalleryId)
            ?? throw new NotFoundException("The image isn't in that gallery any more.");
        var kept = paths.File(keep.FrameName, keep.Name, image.Name, image.FileId);
        if (paths.FullPath(kept) is not { } keptFull || !File.Exists(keptFull))
            throw new InvalidOperationException($"The copy in {keep.Name} isn't there any more, so the others were kept.");

        var files = 0;
        var bytes = 0L;
        foreach (var g in galleries.Where(g => g.Id != keepGalleryId))
        {
            var relative = paths.File(g.FrameName, g.Name, image.Name, image.FileId);
            if (relative == kept || paths.FullPath(relative) is not { } full || !File.Exists(full))
                continue;
            var size = new FileInfo(full).Length;
            if (DeleteQuietly(() => File.Delete(full), relative))
            {
                files++;
                bytes += size;
            }
        }
        if (files > 0)
            logger.LogInformation("Deleted {Count} duplicate copies of {Image}, kept the one in {Gallery}", files, image.Name, keep.Name);
        return (files, bytes);
    }

    // Removes an image whose file is identical to the kept image's: its files in all its galleries'
    // folders and its entries. The kept image stays as it is. A file the kept image uses too (same
    // title in the same folder) stays. If a frame shows the removed image again, it's recorded and
    // downloaded anew. Returns how many files were deleted and their size.
    public async Task<(int Files, long Bytes)> RemoveIdenticalAsync(int keepId, int removeId, CancellationToken ct = default)
    {
        if (keepId == removeId)
            throw new InvalidOperationException("The image to keep can't be removed.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var keep = await db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.Id == keepId, ct)
            ?? throw new NotFoundException("The image to keep was deleted meanwhile.");
        var remove = await db.Images.FirstOrDefaultAsync(i => i.Id == removeId, ct)
            ?? throw new NotFoundException("This image was removed already.");

        var keepFiles = (await GalleriesOfAsync(db, keep, ct))
            .Select(g => paths.File(g.FrameName, g.Name, keep.Name, keep.FileId)).ToHashSet(StringComparer.Ordinal);
        if (!keepFiles.Any(f => paths.FullPath(f) is { } full && File.Exists(full)))
            throw new InvalidOperationException($"The file of {keep.Name} isn't there any more, so nothing was removed.");

        var files = 0;
        var bytes = 0L;
        foreach (var g in await GalleriesOfAsync(db, remove, ct))
        {
            var relative = paths.File(g.FrameName, g.Name, remove.Name, remove.FileId);
            if (keepFiles.Contains(relative) || paths.FullPath(relative) is not { } full || !File.Exists(full))
                continue;
            var size = new FileInfo(full).Length;
            if (DeleteQuietly(() => File.Delete(full), relative))
            {
                files++;
                bytes += size;
            }
        }

        await db.GalleryImages.Where(l => l.ImageId == remove.Id).ExecuteDeleteAsync(ct);
        db.Images.Remove(remove);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Removed {Removed} (Meural id {RemovedId}): identical to {Kept} (Meural id {KeptId}), {Count} files deleted",
            remove.Name, remove.FileId, keep.Name, keep.FileId, files);
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

    // Keeps one copy of each duplicate: the one in the image's own gallery (where it was first shown)
    // when that folder has it, else the first gallery's.
    public async Task<DedupeResult> KeepOneCopyOfEachAsync(IReadOnlyList<Duplicate> duplicates, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        int images = 0, files = 0;
        var bytes = 0L;
        for (var n = 0; n < duplicates.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var d = duplicates[n];
            progress?.Report($"{n + 1:N0} of {duplicates.Count:N0}: {d.Name}");
            var keep = d.Keeper;
            try
            {
                var (f, b) = await KeepOneCopyAsync(d.Id, keep.GalleryId, ct);
                if (f > 0) images++;
                files += f;
                bytes += b;
            }
            catch (Exception ex) when (ex is NotFoundException or InvalidOperationException)
            {
                logger.LogInformation("Kept the copies of {Image}: {Reason}", d.Name, ex.Message);
            }
        }
        return new DedupeResult(images, files, bytes);
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
        // That gallery's copy first, then any other gallery's (a gallery may show another's copy).
        var preferred = inGallery ? galleryId!.Value : image.GalleryId;
        return (await GalleriesOfAsync(db, image, ct))
            .OrderBy(x => x.Id == preferred ? 0 : 1)
            .Select(x => paths.FullPath(paths.File(x.FrameName, x.Name, image.Name, image.FileId)))
            .FirstOrDefault(full => full is not null && File.Exists(full));
    }

    // Brings the database in line with the uploads folder, without removing anything:
    // - a gallery folder without a gallery gets one (without the frames' folders its frame is "Unknown"),
    // - an image file whose image is recorded in another gallery is linked to this one,
    // - an image file nobody recorded gets an entry (with the Meural id when the file is named after it).
    // Files are matched to images by the file name the image would be saved under. When several
    // recorded images could be the file and none is clearly it, the file is skipped.
    public Task<SyncResult> SyncWithDiskAsync(IProgress<string>? progress = null, CancellationToken ct = default) =>
        SyncAsync(apply: true, loose: null, progress, ct);

    // The files in the uploads folder that no image uses and the sync can't place: image files
    // outside a gallery folder, image files whose name several recorded images share, and files
    // that aren't images (other types, empty files, unfinished downloads). Reads only.
    public async Task<IReadOnlyList<LooseFile>> FindLooseFilesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var loose = new List<LooseFile>();
        await SyncAsync(apply: false, loose, progress, ct);
        var others = await Task.Run(() => EnumerateUploads(ct)
            .Where(f => !f.Relative.EndsWith(ImagePaths.Extension, StringComparison.Ordinal) || f.Info.Length == 0)
            .Select(f => new LooseFile(f.Relative, LooseReason.NotAnImage, f.Info.Length, f.Info.LastWriteTimeUtc, []))
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

    // The sync's work. With apply false nothing is written: new galleries and images get temporary
    // ids, so the matching runs exactly as it would, and the skipped files are collected in loose.
    private async Task<SyncResult> SyncAsync(bool apply, List<LooseFile>? loose, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reading the uploads folder");
        var files = await Task.Run(() => ScanFileDetails(progress, ct), ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().ToListAsync(ct);
        var images = await db.Images.AsNoTracking().ToListAsync(ct);
        var links = await db.GalleryImages.AsNoTracking().ToListAsync(ct);

        var members = new HashSet<(int Gallery, int Image)>(images.Select(i => (i.GalleryId, i.Id)).Concat(links.Select(l => (l.GalleryId, l.ImageId))));
        var byFolder = galleries.GroupBy(g => paths.Folder(g.FrameName, g.Name), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(g => g.LastAccessedAt).ToList(), StringComparer.Ordinal);
        var byFileName = images.GroupBy(i => FileNaming.FileName(i.Name, i.FileId), StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var byFileId = images.Where(i => i.FileId != 0).GroupBy(i => i.FileId).ToDictionary(x => x.Key, x => x.First());
        var homeOf = galleries.ToDictionary(g => g.Id);

        var tempId = -1;
        async Task AddAsync<T>(T entity, Action<int> setId) where T : class
        {
            if (!apply)
            {
                setId(tempId--);
                return;
            }
            db.Add(entity);
            await db.SaveChangesAsync(ct);
        }

        int galleriesAdded = 0, imagesAdded = 0, imagesLinked = 0, outside = 0, shared = 0;
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
                    loose?.Add(new LooseFile(file.Relative, LooseReason.OutsideGallery, file.Size, file.Time, []));
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
                await AddAsync(gallery, id => gallery.Id = id);
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
                    await AddAsync(image, id => image.Id = id);
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
                    shared++;
                    loose?.Add(new LooseFile(file.Relative, LooseReason.SharedName, file.Size, file.Time,
                        candidates.Select(c => homeOf.TryGetValue(c.GalleryId, out var home) ? $"{c.Name} ({home.Name}, {home.FrameName})" : c.Name).ToList()));
                    if (apply)
                        logger.LogInformation("Sync skipped {File}: {Count} recorded images have that name", file.Relative, candidates.Count);
                    continue;
                }
                if (apply)
                    db.GalleryImages.Add(new GalleryImage { GalleryId = target.Id, ImageId = match.Id });
                members.Add((target.Id, match.Id));
                imagesLinked++;
            }
        }
        var result = new SyncResult(galleriesAdded, imagesAdded, imagesLinked, outside + shared, outside);
        if (!apply)
            return result;

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Synced with the uploads folder: {Galleries} galleries added, {Images} images added, {Linked} linked, {Outside} files outside gallery folders and {Shared} with a name several images share skipped",
            galleriesAdded, imagesAdded, imagesLinked, outside, shared);
        return result;
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

    // The image's galleries: its own first, then the others in the order RemoveFromGalleryAsync
    // picks the next one from.
    private static async Task<List<Gallery>> GalleriesOfAsync(MeuralDb db, Image image, CancellationToken ct)
    {
        var linked = db.GalleryImages.Where(l => l.ImageId == image.Id).Select(l => l.GalleryId);
        var galleries = await db.Galleries.AsNoTracking().Where(g => g.Id == image.GalleryId || linked.Contains(g.Id)).ToListAsync(ct);
        return galleries.OrderBy(g => g.Id == image.GalleryId ? 0 : 1).ThenBy(g => g.Id).ToList();
    }

    // Before the image leaves the gallery: when that gallery's folder has the only copy of its file
    // and the image stays in other galleries, the file moves to the one it belongs to from then on.
    // Returns the leaving gallery's path when that file must stay where it is (another of the
    // image's galleries uses the same folder), else null.
    private async Task<string?> KeepLastCopyAsync(MeuralDb db, Image image, Gallery leaving, CancellationToken ct)
    {
        var source = paths.File(leaving.FrameName, leaving.Name, image.Name, image.FileId);
        if (paths.FullPath(source) is not { } sourceFull || !File.Exists(sourceFull))
            return null;
        var targets = (await GalleriesOfAsync(db, image, ct))
            .Where(g => g.Id != leaving.Id)
            .Select(g => paths.File(g.FrameName, g.Name, image.Name, image.FileId))
            .ToList();
        if (targets.Count == 0)
            return null;
        if (targets.Contains(source))
            return source;
        if (targets.Any(t => paths.FullPath(t) is { } full && File.Exists(full)))
            return null;
        // The gallery the image belongs to next: its own, or when it leaves that, the first other one.
        if (paths.FullPath(targets[0]) is not { } targetFull)
            return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);
            File.Move(sourceFull, targetFull);
            logger.LogInformation("Moved {From} to {To}: it was the image's last copy", source, targets[0]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Couldn't move {From} to {To}: {Reason}", source, targets[0], ex.Message);
        }
        return null;
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
