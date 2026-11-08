using MeuralImageBackup.Core.Models;
using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// An image in several galleries, and the sync of the database with the uploads folder.
public class SyncTests
{
    private static async Task<int> AddGalleryAsync(DbFactory db, string frame, string name, params (int FileId, string Title)[] images)
    {
        await using var ctx = db.CreateDbContext();
        var now = DateTime.UtcNow;
        var gallery = new Gallery
        {
            FrameName = frame, Name = name, CreatedAt = now, LastAccessedAt = now, AccessCount = 1,
            Images = images.Select(i => new Image { FileId = i.FileId, Name = i.Title, CreatedAt = now, LastAccessedAt = now, AccessCount = 1 }).ToList(),
        };
        ctx.Galleries.Add(gallery);
        await ctx.SaveChangesAsync();
        return gallery.Id;
    }

    private static LibraryService Library(TestStorage storage, DbFactory db, bool useFrameFolder = true) =>
        new(db, storage.ImagePaths(useFrameFolder), NullLogger<LibraryService>.Instance);

    private static UploadService Uploads(TestStorage storage, DbFactory db) =>
        new(db, storage.ImagePaths(), new HttpClient(new FakeImageServer()), NullLogger<UploadService>.Instance);

    [Fact]
    public async Task ARenamedGalleryGetsTheImagesInItsFolder()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "gArt", "People", (1, "Portrait"), (2, "Street"));
        await AddGalleryAsync(db, "gArt", "People [X]");
        storage.WriteUpload("gArt/People/Portrait.jpeg");
        storage.WriteUpload("gArt/People/Street.jpeg");
        storage.WriteUpload("gArt/People [X]/Portrait.jpeg", "copy");
        var library = Library(storage, db);

        var result = await library.SyncWithDiskAsync();

        Assert.Equal(new SyncResult(0, 0, 1, 0), result);
        var loaded = await library.LoadAsync();
        var renamed = loaded.Galleries.Single(g => g.Name == "People [X]");
        var portrait = Assert.Single(renamed.Images);
        Assert.Equal(("Portrait", true, 4), (portrait.Name, portrait.Exists, portrait.Size));
        Assert.Equal(2, loaded.Galleries.Single(g => g.Name == "People").Images.Count);
        Assert.Equal(3, loaded.Images.Select(i => i.Key).Distinct().Count());

        // A second sync finds nothing to do.
        Assert.False((await library.SyncWithDiskAsync()).Changed);
    }

    [Fact]
    public async Task FoldersAndFilesNobodyRecordedAreAdded()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("gArt/New [X]/Pic [1].jpeg");
        storage.WriteUpload("gArt/New [X]/1234.jpeg");
        storage.WriteUpload("gArtMini/New [X]/Pic [1].jpeg");
        storage.WriteUpload("gArt/New [X]/notes.txt");
        storage.WriteUpload("stray.jpeg");
        var library = Library(storage, db);

        var result = await library.SyncWithDiskAsync();

        // Pic [1] in the second frame's folder is the same file name, so the same image, linked.
        Assert.Equal(new SyncResult(2, 2, 1, 1), result);
        await using var ctx = db.CreateDbContext();
        Assert.Equal(1234, (await ctx.Images.SingleAsync(i => i.Name == "1234")).FileId);
        var loaded = await library.LoadAsync();
        Assert.All(loaded.Images, i => Assert.True(i.Exists));
        Assert.Equal(new[] { 2, 1 }, loaded.Galleries.OrderBy(g => g.FrameName).Select(g => g.Images.Count));
    }

    [Fact]
    public async Task WithoutTheFrameFolderAnAddedGalleryIsOnFrameUnknown()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("Found/a.jpeg");
        var library = Library(storage, db, useFrameFolder: false);

        Assert.Equal(new SyncResult(1, 1, 0, 0), await library.SyncWithDiskAsync());
        var gallery = Assert.Single((await library.LoadAsync()).Galleries);
        Assert.Equal(("Unknown", "Found", true), (gallery.FrameName, gallery.Name, gallery.Images.Single().Exists));
    }

    [Fact]
    public async Task AFileMatchingImagesOnOtherFramesIsSkipped()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "One", "G", (1, "Same"));
        await AddGalleryAsync(db, "Two", "G", (2, "Same"));
        await AddGalleryAsync(db, "Three", "G");
        storage.WriteUpload("Three/G/Same.jpeg");

        Assert.Equal(new SyncResult(0, 0, 0, 1), await Library(storage, db).SyncWithDiskAsync());
    }

    [Fact]
    public async Task AnImageShownFromAnotherGalleryIsListedInBoth()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db);

        await uploads.HandleAsync(new MeuralUpload("gArt", "Portrait", "People", "https://x/1.jpg", 1));
        await uploads.HandleAsync(new MeuralUpload("gArt", "Portrait", "People [X]", "https://x/1.jpg", 1));
        await uploads.HandleAsync(new MeuralUpload("gArt", "Portrait", "People [X]", "https://x/1.jpg", 1));

        var loaded = await Library(storage, db).LoadAsync();
        Assert.All(loaded.Galleries, g => Assert.True(Assert.Single(g.Images).Exists));
        await using var ctx = db.CreateDbContext();
        Assert.Equal(3, (await ctx.Images.SingleAsync()).AccessCount);
        Assert.Equal(1, await ctx.GalleryImages.CountAsync());
    }

    [Fact]
    public async Task ShowingAnImageTheSyncAddedGivesItItsMeuralId()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("gArt/G/Pic.jpeg");
        await Library(storage, db).SyncWithDiskAsync();

        await Uploads(storage, db).HandleAsync(new MeuralUpload("gArt", "Pic", "G", "https://x/77.jpg", 77));

        await using var ctx = db.CreateDbContext();
        var image = await ctx.Images.SingleAsync();
        Assert.Equal((77, 1), (image.FileId, image.AccessCount));
    }

    [Fact]
    public async Task DeletingTheOldGalleryKeepsTheImageInTheNewOne()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var old = await AddGalleryAsync(db, "gArt", "People", (1, "Portrait"));
        await AddGalleryAsync(db, "gArt", "People [X]");
        storage.WriteUpload("gArt/People/Portrait.jpeg");
        storage.WriteUpload("gArt/People [X]/Portrait.jpeg");
        var library = Library(storage, db);
        await library.SyncWithDiskAsync();

        await library.DeleteGalleryAsync(old);

        Assert.False(Directory.Exists(storage.Upload("gArt/People")));
        var gallery = Assert.Single((await library.LoadAsync()).Galleries);
        Assert.Equal("People [X]", gallery.Name);
        Assert.True(Assert.Single(gallery.Images).Exists);
        await using var ctx = db.CreateDbContext();
        Assert.Equal(0, await ctx.GalleryImages.CountAsync());
    }

    [Fact]
    public async Task DeletingAnImageFromOneGalleryKeepsItInTheOther()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "gArt", "People", (1, "Portrait"));
        await AddGalleryAsync(db, "gArt", "People [X]");
        storage.WriteUpload("gArt/People/Portrait.jpeg");
        storage.WriteUpload("gArt/People [X]/Portrait.jpeg");
        var library = Library(storage, db);
        await library.SyncWithDiskAsync();
        var original = (await library.LoadAsync()).Images.Single(i => i.GalleryName == "People");

        await library.DeleteImageAsync(original.Id, original.GalleryId);

        Assert.False(File.Exists(storage.Upload("gArt/People/Portrait.jpeg")));
        var left = Assert.Single((await library.LoadAsync()).Images);
        Assert.Equal(("People [X]", true), (left.GalleryName, left.Exists));
    }

    [Fact]
    public async Task RemovingMissingFilesWorksPerGallery()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "gArt", "People", (1, "Portrait"));
        await AddGalleryAsync(db, "gArt", "People [X]");
        storage.WriteUpload("gArt/People/Portrait.jpeg");
        storage.WriteUpload("gArt/People [X]/Portrait.jpeg");
        var library = Library(storage, db);
        await library.SyncWithDiskAsync();
        File.Delete(storage.Upload("gArt/People/Portrait.jpeg"));

        Assert.Equal(1, await library.RemoveMissingAsync());
        var left = Assert.Single((await library.LoadAsync()).Images);
        Assert.Equal("People [X]", left.GalleryName);
    }
}
