using MeuralImageBackup.Core.Models;
using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// One image per gallery: the same Meural file shown in two galleries is two images, each
// with its own file; the one-time cleanup of images listed in several galleries (pre-release builds);
// and different images with identical files.
public class DuplicateTests
{
    private const string Bytes = "jpeg-bytes";

    private static UploadService Uploads(TestStorage storage, DbFactory db, FakeImageServer server) =>
        new(db, storage.ImagePaths(), new HttpClient(server), NullLogger<UploadService>.Instance);

    private static LibraryService Library(TestStorage storage, DbFactory db) =>
        new(db, storage.ImagePaths(), NullLogger<LibraryService>.Instance);

    private static MeuralUpload Shown(string gallery, int id) =>
        new("Living room", $"Image {id}", gallery, $"https://images.example/{id}.jpg", id);

    [Fact]
    public async Task AnImageShownInAnotherGalleryIsAnotherImageWithItsOwnFile()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();
        var uploads = Uploads(storage, db, server);

        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        await uploads.HandleAsync(Shown("B", 1));

        Assert.Equal(2, server.Requests);
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        var loaded = await Library(storage, db).LoadAsync();
        Assert.Equal([("A", 1), ("B", 2)], loaded.Images.Select(i => (i.GalleryName, i.AccessCount)).Order());
        Assert.All(loaded.Images, i => Assert.Equal(1, i.FileId));
        Assert.Null(loaded.Legacy);
    }

    [Fact]
    public async Task DeletingAnImageLeavesTheSameFileInAnotherGallery()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        var library = Library(storage, db);
        var inA = (await library.LoadAsync()).Images.Single(i => i.GalleryName == "A");

        await library.DeleteImageAsync(inA.Id);

        Assert.False(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        var left = Assert.Single((await library.LoadAsync()).Images);
        Assert.Equal(("B", true), (left.GalleryName, left.Exists));
    }

    [Fact]
    public async Task DeletingAGalleryLeavesTheSameFileInAnotherGallery()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        var library = Library(storage, db);
        var a = (await library.LoadAsync()).Galleries.Single(g => g.Name == "A");

        await library.DeleteGalleryAsync(a.Id);

        Assert.False(Directory.Exists(storage.Upload("Living room/A")));
        Assert.Equal("B", Assert.Single((await library.LoadAsync()).Images).GalleryName);
    }

    // Image 1 belongs to A and is linked to B and C, as the pre-release builds recorded it; image 2 in A.
    // ownFile and copies say which of the three folders have image 1's file.
    private static async Task<(DbFactory Db, LibraryService Library, int ImageId)> LegacyAsync(TestStorage storage, bool ownFile = true, bool copyInB = true, bool copyInC = true)
    {
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("A", 2));
        await uploads.HandleAsync(Shown("B", 3));
        await uploads.HandleAsync(Shown("C", 4));
        if (!ownFile)
            File.Delete(storage.Upload("Living room/A/Image 1.jpeg"));
        if (copyInB)
            storage.WriteUpload("Living room/B/Image 1.jpeg", Bytes);
        if (copyInC)
            storage.WriteUpload("Living room/C/Image 1.jpeg", Bytes);

        await using var ctx = db.CreateDbContext();
        var image = await ctx.Images.SingleAsync(i => i.FileId == 1);
        foreach (var name in new[] { "B", "C" })
        {
            var gallery = await ctx.Galleries.SingleAsync(g => g.Name == name);
            ctx.GalleryImages.Add(new GalleryImage { GalleryId = gallery.Id, ImageId = image.Id });
        }
        await ctx.SaveChangesAsync();
        return (db, Library(storage, db), image.Id);
    }

    [Fact]
    public async Task ImagesListedInSeveralGalleriesAreReportedAndShownOnlyInTheirOwn()
    {
        using var storage = new TestStorage();
        var (_, library, _) = await LegacyAsync(storage);

        var loaded = await library.LoadAsync();

        Assert.Equal(new LegacyCleanup(1, 2, 2L * Bytes.Length), loaded.Legacy);
        Assert.Equal("A", Assert.Single(loaded.Images, i => i.FileId == 1).GalleryName);
    }

    [Fact]
    public async Task TheCleanupKeepsTheImageInItsOwnGalleryAndDeletesTheOtherCopies()
    {
        using var storage = new TestStorage();
        var (db, library, id) = await LegacyAsync(storage);

        var result = await library.CleanUpLegacyAsync();

        Assert.Equal(new DedupeResult(1, 2, 2L * Bytes.Length), result);
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));
        // The other images are untouched.
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 3.jpeg")));
        await using var ctx = db.CreateDbContext();
        Assert.Empty(await ctx.GalleryImages.ToListAsync());
        var loaded = await library.LoadAsync();
        Assert.Null(loaded.Legacy);
        Assert.Equal(("A", true), loaded.Images.Where(i => i.Id == id).Select(i => (i.GalleryName, i.Exists)).Single());
        Assert.Equal(4, loaded.Images.Count);
    }

    [Fact]
    public async Task TheCleanupMovesAnImageWhoseOwnFolderLacksItsFile()
    {
        using var storage = new TestStorage();
        var (_, library, id) = await LegacyAsync(storage, ownFile: false);

        Assert.Equal(new LegacyCleanup(1, 1, Bytes.Length), (await library.LoadAsync()).Legacy);
        await library.CleanUpLegacyAsync();

        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));
        var image = (await library.LoadAsync()).Images.Single(i => i.Id == id);
        Assert.Equal(("B", true), (image.GalleryName, image.Exists));
    }

    [Fact]
    public async Task TheCleanupOnlyDropsTheLinksWhenNoFolderHasTheFile()
    {
        using var storage = new TestStorage();
        var (_, library, id) = await LegacyAsync(storage, ownFile: false, copyInB: false, copyInC: false);

        var result = await library.CleanUpLegacyAsync();

        Assert.Equal(new DedupeResult(1, 0, 0), result);
        var image = (await library.LoadAsync()).Images.Single(i => i.Id == id);
        Assert.Equal(("A", false), (image.GalleryName, image.Exists));
    }

    [Fact]
    public async Task TheCleanupNeverDeletesAFileAnotherImageUses()
    {
        using var storage = new TestStorage();
        var (db, library, _) = await LegacyAsync(storage);
        // B got its own entry for the same Meural file (shown there after the links were dropped
        // from registration, or added by hand), which uses B's copy.
        await using (var ctx = db.CreateDbContext())
        {
            var b = await ctx.Galleries.SingleAsync(g => g.Name == "B");
            ctx.Images.Add(new Image { GalleryId = b.Id, FileId = 1, Name = "Image 1", AccessCount = 1, CreatedAt = DateTime.UtcNow, LastAccessedAt = DateTime.UtcNow });
            await ctx.SaveChangesAsync();
        }

        var result = await library.CleanUpLegacyAsync();

        Assert.Equal(1, result.FilesDeleted);
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));
        var inB = (await library.LoadAsync()).Galleries.Single(g => g.Name == "B").Images.Single(i => i.FileId == 1);
        Assert.True(inB.Exists);
    }

    [Fact]
    public async Task BeforeTheCleanupALinkedImageIsCountedAndSyncLeavesItsCopies()
    {
        using var storage = new TestStorage();
        var (db, library, id) = await LegacyAsync(storage);

        await Uploads(storage, db, new FakeImageServer()).HandleAsync(Shown("B", 1));
        var sync = await library.SyncWithDiskAsync();

        Assert.Equal(new SyncResult(0, 0, 0), sync);
        await using var ctx = db.CreateDbContext();
        Assert.Equal(2, (await ctx.Images.SingleAsync(i => i.Id == id)).AccessCount);
        Assert.Equal(1, await ctx.Images.CountAsync(i => i.FileId == 1));
    }

    private static IdenticalFilesService Identical(TestStorage storage) => new(storage.ImagePaths());

    [Fact]
    public async Task ImagesWithTheSameFileAreFoundAcrossGalleries()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        // Every fake download has the same bytes.
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        await uploads.HandleAsync(Shown("A", 2));

        var group = Assert.Single(await Identical(storage).FindAsync(await Library(storage, db).LoadAsync()));

        Assert.Equal(3, group.Images.Count);
        Assert.Equal(("Image 1", 2), (group.Keeper.Name, group.Keeper.AccessCount));
        Assert.Equal("A", Assert.Single(group.Keeper.Copies).GalleryName);
        Assert.Equal(2L * Bytes.Length, group.Wasted);
    }

    [Fact]
    public async Task FilesThatDifferAreNotIdenticalFiles()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("A", 2));
        // Same size, other bytes.
        File.WriteAllText(storage.Upload("Living room/A/Image 2.jpeg"), "JPEG-BYTES");

        Assert.Empty(await Identical(storage).FindAsync(await Library(storage, db).LoadAsync()));
    }

    [Fact]
    public async Task KeepingOneRemovesTheOthersAndLeavesTheKeptImageAsItIs()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("D", 2));
        var library = Library(storage, db);
        var group = Assert.Single(await Identical(storage).FindAsync(await library.LoadAsync()));

        var (files, bytes) = await library.RemoveIdenticalAsync(group.Keeper.Id, group.Images[1].Id);

        Assert.Equal((1, (long)Bytes.Length), (files, bytes));
        Assert.False(File.Exists(storage.Upload("Living room/D/Image 2.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        var loaded = await library.LoadAsync();
        Assert.DoesNotContain(loaded.Images, i => i.Name == "Image 2");
        // Not a merge: D doesn't list image 1, and its count is its own.
        Assert.Empty(loaded.Galleries.Single(g => g.Name == "D").Images);
        var kept = Assert.Single(loaded.Images);
        Assert.Equal(("Image 1", "A", 2), (kept.Name, kept.GalleryName, kept.AccessCount));
        Assert.Empty(await Identical(storage).FindAsync(loaded));
    }

    [Fact]
    public async Task ImagesSharingOneFileAreGroupedAndKeepingOneDeletesNoFile()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        // Two Meural ids, same title, same gallery: one file.
        await uploads.HandleAsync(new MeuralUpload("Living room", "Basilica", "A", "https://x/1.jpg", 1));
        await uploads.HandleAsync(new MeuralUpload("Living room", "Basilica", "A", "https://x/2.jpg", 2));
        var library = Library(storage, db);
        var group = Assert.Single(await Identical(storage).FindAsync(await library.LoadAsync()));
        Assert.True(group.SharedFile);
        Assert.Equal(0, group.Wasted);

        var (files, _) = await library.RemoveIdenticalAsync(group.Keeper.Id, group.Images[1].Id);

        Assert.Equal(0, files);
        Assert.True(File.Exists(storage.Upload("Living room/A/Basilica.jpeg")));
        Assert.True(Assert.Single((await library.LoadAsync()).Images).Exists);
    }

    [Fact]
    public async Task KeepingOneOfEachKeepsTheMostShownImageOfEachGroup()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        await uploads.HandleAsync(Shown("B", 1));
        await uploads.HandleAsync(Shown("A", 2));
        var library = Library(storage, db);
        var groups = await Identical(storage).FindAsync(await library.LoadAsync());

        var result = await library.RemoveIdenticalAsync(groups);

        Assert.Equal(new DedupeResult(2, 2, 2L * Bytes.Length), result);
        var kept = Assert.Single((await library.LoadAsync()).Images);
        Assert.Equal(("Image 1", "B", 2), (kept.Name, kept.GalleryName, kept.AccessCount));
        Assert.False(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/A/Image 2.jpeg")));
    }

    [Fact]
    public async Task NothingIsRemovedWhenTheKeptFileIsGone()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("D", 2));
        var library = Library(storage, db);
        var group = Assert.Single(await Identical(storage).FindAsync(await library.LoadAsync()));
        foreach (var copy in group.Keeper.Copies)
            File.Delete(storage.Upload(copy.RelativePath));

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.RemoveIdenticalAsync(group.Keeper.Id, group.Images[1].Id));

        Assert.True(File.Exists(storage.Upload(group.Images[1].Copies[0].RelativePath)));
    }

    [Fact]
    public async Task SyncReportsProgress()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("Frame/G/One.jpeg");
        var reports = new List<string>();

        await Library(storage, db).SyncWithDiskAsync(new SyncProgress(reports));

        Assert.Contains(reports, r => r.StartsWith("Checking folder 1 of 1", StringComparison.Ordinal));
    }

    // Reports synchronously (Progress<T> posts them to the thread pool).
    private sealed class SyncProgress(List<string> reports) : IProgress<string>
    {
        public void Report(string value) => reports.Add(value);
    }
}
