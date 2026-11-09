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
// An image belongs to the gallery it's shown in: shown in another gallery, the same Meural file is
// recorded there as another image and saved in that gallery's folder too (as MeuralImageUploader
// saved it; since 2.5.0 the entry is kept per gallery as well).
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

    // One image per Meural file id and gallery; without an id nothing is recorded. An entry the sync
    // made from a file without knowing the id (FileId 0) gets the id when the frame shows that image
    // in that gallery. An image listed in this gallery through a legacy link (2.1.0 to 2.4.1, see
    // LegacyCleanup) is counted there, so no second entry appears before the cleanup.
    internal async Task RegisterImageAsync(int galleryId, int? fileId, string? name, CancellationToken ct)
    {
        if (fileId is null)
            return;
        name ??= fileId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var linked = db.GalleryImages.Where(l => l.GalleryId == galleryId).Select(l => l.ImageId);
        var image = await db.Images.Where(i => i.FileId == fileId && (i.GalleryId == galleryId || linked.Contains(i.Id)))
                .OrderBy(i => i.GalleryId == galleryId ? 0 : 1).ThenBy(i => i.Id).FirstOrDefaultAsync(ct)
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
        }
        await db.SaveChangesAsync(ct);
    }

    // An entry the sync made in this gallery for a file of this name, without a Meural id yet.
    private static async Task<Image?> FromSyncAsync(MeuralDb db, int galleryId, string fileName, CancellationToken ct)
    {
        var candidates = await db.Images.Where(i => i.FileId == 0 && i.GalleryId == galleryId).ToListAsync(ct);
        return candidates.FirstOrDefault(i => FileNaming.FileName(i.Name, 0) == fileName);
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
