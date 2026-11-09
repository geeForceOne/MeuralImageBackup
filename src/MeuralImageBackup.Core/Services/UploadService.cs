using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeuralImageBackup.Core.Services;

// What Home Assistant posts to /api/meural when a frame shows an image. All optional, as before.
public sealed record MeuralUpload(string? FrameName, string? Title, string? Gallery, string? Url, int? Id);

// Records a shown image and keeps a copy of it: MeuralImageUploader's /api/meural, with the same
// counting and file layout. Uploads are handled one at a time, so two frames reporting at once
// can't count a gallery twice.
// Each image is stored once (since 2.3.1; before, optional): when a frame shows an image from
// another gallery and its file is in one of its galleries' folders already, it isn't downloaded
// again; the new gallery lists it and shows the stored copy.
public sealed class UploadService(IDbContextFactory<MeuralDb> dbFactory, ImagePaths paths, HttpClient http, ILogger<UploadService> logger)
{

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task HandleAsync(MeuralUpload upload, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var galleryId = await RegisterGalleryAsync(upload.FrameName, upload.Gallery, ct);
            if (galleryId is not null)
                await RegisterImageAsync(galleryId.Value, upload.Id, upload.Title, ct);
        }
        finally
        {
            Gate.Release();
        }

        await DownloadAsync(upload, ct);
    }

    // The gallery's id, or null without a frame name (the image is then downloaded but not recorded).
    // Its access count goes up when it's new or the frame switched to it from another gallery.
    internal async Task<int?> RegisterGalleryAsync(string? frameName, string? name, CancellationToken ct)
    {
        if (frameName is null)
            return null;
        name ??= "Unknown";
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var switched = !await db.LastGalleries.AnyAsync(g => g.FrameName == frameName && g.Name == name, ct);
        if (switched)
        {
            await db.LastGalleries.Where(g => g.FrameName == frameName).ExecuteDeleteAsync(ct);
            db.LastGalleries.Add(new LastGallery { Name = name, FrameName = frameName });
        }

        var gallery = await db.Galleries.FirstOrDefaultAsync(g => g.FrameName == frameName && g.Name == name, ct);
        if (gallery is null)
        {
            gallery = new Gallery { Name = name, FrameName = frameName, AccessCount = 1, CreatedAt = now, LastAccessedAt = now };
            db.Galleries.Add(gallery);
        }
        else if (switched)
        {
            gallery.AccessCount++;
            gallery.LastAccessedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return gallery.Id;
    }

    // An image is one row per Meural file id, wherever it's shown later; without an id nothing is recorded.
    // Shown from another gallery than its own, it's linked to that gallery too (its file is saved there
    // as well). An entry the sync made from a file without knowing the id (FileId 0) gets the id when
    // the frame shows that image in that gallery.
    internal async Task RegisterImageAsync(int galleryId, int? fileId, string? name, CancellationToken ct)
    {
        if (fileId is null)
            return;
        name ??= fileId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.FirstOrDefaultAsync(i => i.FileId == fileId, ct)
            ?? await FromSyncAsync(db, galleryId, FileNaming.FileName(name, fileId), ct);
        if (image is null)
        {
            db.Images.Add(new Image { GalleryId = galleryId, FileId = fileId.Value, Name = name, AccessCount = 1, CreatedAt = now, LastAccessedAt = now });
        }
        else
        {
            image.FileId = fileId.Value;
            image.AccessCount++;
            image.LastAccessedAt = now;
            if (image.GalleryId != galleryId &&
                !await db.GalleryImages.AnyAsync(l => l.GalleryId == galleryId && l.ImageId == image.Id, ct))
                db.GalleryImages.Add(new GalleryImage { GalleryId = galleryId, ImageId = image.Id });
        }
        await db.SaveChangesAsync(ct);
    }

    // An entry the sync made in this gallery for a file of this name, without a Meural id yet.
    private static async Task<Image?> FromSyncAsync(MeuralDb db, int galleryId, string fileName, CancellationToken ct)
    {
        var linked = db.GalleryImages.Where(l => l.GalleryId == galleryId).Select(l => l.ImageId);
        var candidates = await db.Images
            .Where(i => i.FileId == 0 && (i.GalleryId == galleryId || linked.Contains(i.Id)))
            .ToListAsync(ct);
        return candidates.FirstOrDefault(i => FileNaming.FileName(i.Name, 0) == fileName);
    }

    // Where the recorded image with this Meural id has a file, in any of its galleries' folders; null
    // when it has none.
    private async Task<string?> StoredCopyAsync(int fileId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var image = await db.Images.AsNoTracking().FirstOrDefaultAsync(i => i.FileId == fileId, ct);
        if (image is null)
            return null;
        var linked = db.GalleryImages.Where(l => l.ImageId == image.Id).Select(l => l.GalleryId);
        var galleries = await db.Galleries.AsNoTracking()
            .Where(g => g.Id == image.GalleryId || linked.Contains(g.Id))
            .Select(g => new { g.FrameName, g.Name })
            .ToListAsync(ct);
        return galleries
            .Select(g => paths.File(g.FrameName, g.Name, image.Name, image.FileId))
            .FirstOrDefault(r => paths.FullPath(r) is { } full && File.Exists(full));
    }

    // Downloads the image unless its file is there already. Goes to a temporary file first, so a
    // failed or cut-off download leaves nothing behind; an empty or failed answer isn't kept.
    private async Task DownloadAsync(MeuralUpload upload, CancellationToken ct)
    {
        var relative = paths.File(upload.FrameName, upload.Gallery, upload.Title, upload.Id);
        var path = paths.FullPath(relative);
        if (path is null)
        {
            logger.LogWarning("Not saving {File}: the name leads outside the uploads folder", relative);
            return;
        }
        if (File.Exists(path))
            return;
        if (upload.FrameName is not null && upload.Id is { } fileId &&
            await StoredCopyAsync(fileId, ct) is { } stored)
        {
            logger.LogInformation("Not saving {File}: the image is stored as {Stored} already", relative, stored);
            return;
        }
        if (!Uri.TryCreate(upload.Url, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            logger.LogWarning("Not saving {File}: no usable image URL ({Url})", relative, upload.Url);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + ".part";
        try
        {
            logger.LogInformation("Saving {File}", relative);
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Couldn't download {File}: the server answered {Status}", relative, (int)response.StatusCode);
                    return;
                }
                await using var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
                await response.Content.CopyToAsync(file, ct);
            }
            if (new FileInfo(part).Length == 0)
            {
                logger.LogWarning("Couldn't download {File}: the image was empty", relative);
                return;
            }
            File.Move(part, path, overwrite: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Couldn't download {File}: {Reason}", relative, ex.Message);
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { }
        }
    }
}
