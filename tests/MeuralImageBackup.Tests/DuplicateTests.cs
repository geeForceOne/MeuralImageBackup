using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// Images stored in more than one gallery's folder: finding them, keeping one copy, deletes that
// don't lose the last copy, and storing each image once.
public class DuplicateTests
{
    private static UploadService Uploads(TestStorage storage, DbFactory db, FakeImageServer server) =>
        new(db, storage.ImagePaths(), new HttpClient(server), NullLogger<UploadService>.Instance);

    private static LibraryService Library(TestStorage storage, DbFactory db) =>
        new(db, storage.ImagePaths(), NullLogger<LibraryService>.Instance);

    private static MeuralUpload Shown(string gallery, int id) =>
        new("Living room", $"Image {id}", gallery, $"https://images.example/{id}.jpg", id);

    // Image 1 shown in A, then in B and C, with a copy in each folder (as versions before 2.3.1
    // saved it), A is its own gallery; image 2 in A.
    private static async Task<(DbFactory Db, LibraryService Library)> ThreeCopiesAsync(TestStorage storage)
    {
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
        await uploads.HandleAsync(Shown("C", 1));
        await uploads.HandleAsync(Shown("A", 2));
        storage.WriteUpload("Living room/B/Image 1.jpeg", "jpeg-bytes");
        storage.WriteUpload("Living room/C/Image 1.jpeg", "jpeg-bytes");
        return (db, Library(storage, db));
    }

    [Fact]
    public async Task ImagesWithSeveralCopiesAreListed()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);

        var loaded = await library.LoadAsync();

        var dupe = Assert.Single(loaded.Duplicates);
        Assert.Equal("Image 1", dupe.Name);
        Assert.Equal(3, dupe.Copies.Count());
        Assert.Equal("A", dupe.Keeper.GalleryName);
        Assert.True(dupe.Keeper.IsHome);
        Assert.Equal(2 * "jpeg-bytes".Length, dupe.Wasted);
    }

    [Fact]
    public async Task KeepingOneCopyDeletesTheOthersAndTheGalleriesShowIt()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);
        var b = (await library.LoadAsync()).Galleries.Single(g => g.Name == "B");

        var (files, bytes) = await library.KeepOneCopyAsync(b.Images[0].Id, b.Id);

        Assert.Equal((2, 2L * "jpeg-bytes".Length), (files, bytes));
        Assert.False(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));

        var loaded = await library.LoadAsync();
        Assert.Empty(loaded.Duplicates);
        var inA = loaded.Galleries.Single(g => g.Name == "A").Images.Single(i => i.Name == "Image 1");
        Assert.Equal((true, "B", "Living room/B/Image 1.jpeg"), (inA.Exists, inA.StoredInGalleryName, inA.RelativePath));
        Assert.Equal(0, loaded.Images.Count(i => !i.Exists));
        // The shared copy counts once on disk, in B.
        Assert.Equal(0, loaded.Galleries.Single(g => g.Name == "C").Size);

        // The thumbnail of A's listing finds B's file.
        Assert.Equal(storage.Upload("Living room/B/Image 1.jpeg"), await library.FilePathAsync(inA.Id, inA.GalleryId));
    }

    [Fact]
    public async Task KeepingOneOfEachKeepsTheCopyWhereItWasFirstShown()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);

        var result = await library.KeepOneCopyOfEachAsync((await library.LoadAsync()).Duplicates);

        Assert.Equal(new DedupeResult(1, 2, 2L * "jpeg-bytes".Length), result);
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.False(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 2.jpeg")));
    }

    [Fact]
    public async Task KeepingACopyThatIsGoneDeletesNothing()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);
        var b = (await library.LoadAsync()).Galleries.Single(g => g.Name == "B");
        File.Delete(storage.Upload("Living room/B/Image 1.jpeg"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => library.KeepOneCopyAsync(b.Images[0].Id, b.Id));

        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));
    }

    [Fact]
    public async Task DeletingTheGalleryWithTheOnlyCopyMovesItToTheNextGallery()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);
        var a = (await library.LoadAsync()).Galleries.Single(g => g.Name == "A");
        await library.KeepOneCopyAsync(a.Images.Single(i => i.Name == "Image 1").Id, a.Id);

        await library.DeleteGalleryAsync(a.Id);

        Assert.False(Directory.Exists(storage.Upload("Living room/A")));
        // The image now belongs to B (the next gallery), and its file went along.
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        var loaded = await library.LoadAsync();
        Assert.All(loaded.Images.Where(i => i.Name == "Image 1"), i => Assert.True(i.Exists));
        Assert.Equal("B", loaded.Images.Single(i => i.Name == "Image 1" && i.HasOwnCopy).GalleryName);
    }

    [Fact]
    public async Task DeletingAnImageFromTheGalleryWithTheOnlyCopyKeepsTheFileForTheOthers()
    {
        using var storage = new TestStorage();
        var (_, library) = await ThreeCopiesAsync(storage);
        var c = (await library.LoadAsync()).Galleries.Single(g => g.Name == "C");
        var id = c.Images[0].Id;
        await library.KeepOneCopyAsync(id, c.Id);

        await library.DeleteImageAsync(id, c.Id);

        Assert.False(File.Exists(storage.Upload("Living room/C/Image 1.jpeg")));
        Assert.True(File.Exists(storage.Upload("Living room/A/Image 1.jpeg")));
        var loaded = await library.LoadAsync();
        Assert.Equal(["A", "B"], loaded.Images.Where(i => i.Id == id).Select(i => i.GalleryName).Order());
        Assert.All(loaded.Images.Where(i => i.Id == id), i => Assert.True(i.Exists));
    }

    [Fact]
    public async Task AStoredImageIsNotDownloadedAgainForAnotherGallery()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();
        var uploads = Uploads(storage, db, server);

        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));

        Assert.Equal(1, server.Requests);
        Assert.False(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        var loaded = await Library(storage, db).LoadAsync();
        var inB = loaded.Galleries.Single(g => g.Name == "B").Images.Single();
        Assert.Equal((true, "A"), (inB.Exists, inB.StoredInGalleryName));
        Assert.Empty(loaded.Duplicates);
    }

    [Fact]
    public async Task AnImageWhoseFileIsGoneIsDownloadedAgain()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();
        var uploads = Uploads(storage, db, server);
        await uploads.HandleAsync(Shown("A", 1));
        File.Delete(storage.Upload("Living room/A/Image 1.jpeg"));

        await uploads.HandleAsync(Shown("B", 1));

        Assert.Equal(2, server.Requests);
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
        await using var ctx = db.CreateDbContext();
        Assert.Equal(1, await ctx.GalleryImages.CountAsync());
    }

    private static IdenticalFilesService Identical(TestStorage storage) => new(storage.ImagePaths());

    [Fact]
    public async Task DifferentImagesWithTheSameFileAreFound()
    {
        using var storage = new TestStorage();
        // Image 1 in A, B and C (shown 3 times), image 2 in A: every fake download has the same bytes.
        var (_, library) = await ThreeCopiesAsync(storage);

        var groups = await Identical(storage).FindAsync(await library.LoadAsync());

        var group = Assert.Single(groups);
        Assert.Equal(["Image 1", "Image 2"], group.Images.Select(i => i.Name));
        Assert.Equal("Image 1", group.Keeper.Name);
        Assert.Equal(3, group.Keeper.Copies.Count);
        Assert.Equal("jpeg-bytes".Length, group.Wasted);
    }

    [Fact]
    public async Task OneImageInSeveralGalleriesOrFilesThatDifferAreNotIdenticalFiles()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var uploads = Uploads(storage, db, new FakeImageServer());
        await uploads.HandleAsync(Shown("A", 1));
        await uploads.HandleAsync(Shown("B", 1));
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

        Assert.Equal((1, (long)"jpeg-bytes".Length), (files, bytes));
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
        var (db, library) = await ThreeCopiesAsync(storage);
        var groups = await Identical(storage).FindAsync(await library.LoadAsync());

        var result = await library.RemoveIdenticalAsync(groups);

        Assert.Equal(new DedupeResult(1, 1, "jpeg-bytes".Length), result);
        await using var ctx = db.CreateDbContext();
        var image = await ctx.Images.SingleAsync();
        Assert.Equal(("Image 1", 3), (image.Name, image.AccessCount));
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
