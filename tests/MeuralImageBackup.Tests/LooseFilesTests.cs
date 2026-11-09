using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// Files in the uploads folder that no image uses: listing them (the same matching as the sync, without
// writing anything) and deleting them, never a file an image uses.
public class LooseFilesTests
{
    private static LibraryService Library(TestStorage storage, DbFactory db) =>
        new(db, storage.ImagePaths(), NullLogger<LibraryService>.Instance);

    // "Rose" recorded on two frames, plus the files a sync can't place.
    private static async Task<(DbFactory Db, LibraryService Library)> SetupAsync(TestStorage storage)
    {
        var db = await storage.MigratedDbAsync();
        var uploads = new UploadService(db, storage.ImagePaths(), new HttpClient(new FakeImageServer()), NullLogger<UploadService>.Instance);
        await uploads.HandleAsync(new MeuralUpload("Hall", "Rose", "Flowers", "https://x/1.jpg", 1));
        await uploads.HandleAsync(new MeuralUpload("Office", "Rose", "Garden", "https://x/2.jpg", 2));
        storage.WriteUpload("stray.jpeg");
        storage.WriteUpload("Hall/in-frame-folder.jpeg");
        storage.WriteUpload("Hall/Flowers/old/deep.jpeg");
        storage.WriteUpload("Hall/Flowers/Rose.jpeg.part");
        storage.WriteUpload("Hall/Flowers/notes.txt");
        storage.WriteUpload("Hall/Flowers/empty.jpeg", "");
        // Files a sync would add as new images: not loose. Rose in another gallery's folder is an
        // image of that gallery, even with a name images on two frames share.
        storage.WriteUpload("Hall/Flowers/Tulip.jpeg");
        storage.WriteUpload("Kitchen/Herbs/Rose.jpeg");
        return (db, Library(storage, db));
    }

    [Fact]
    public async Task LooseFilesAreListedWithTheirReason()
    {
        using var storage = new TestStorage();
        var (db, library) = await SetupAsync(storage);

        var loose = await library.FindLooseFilesAsync();

        Assert.Equal(
            [
                ("Hall/Flowers/old/deep.jpeg", LooseReason.OutsideGallery),
                ("Hall/in-frame-folder.jpeg", LooseReason.OutsideGallery),
                ("stray.jpeg", LooseReason.OutsideGallery),
                ("Hall/Flowers/empty.jpeg", LooseReason.NotAnImage),
                ("Hall/Flowers/notes.txt", LooseReason.NotAnImage),
                ("Hall/Flowers/Rose.jpeg.part", LooseReason.NotAnImage),
            ],
            loose.Select(f => (f.RelativePath, f.Reason)));

        // Listing writes nothing: the would-be gallery and image aren't added.
        await using var ctx = db.CreateDbContext();
        Assert.Equal(2, await ctx.Galleries.CountAsync());
        Assert.Equal(2, await ctx.Images.CountAsync());
    }

    [Fact]
    public async Task TheSyncCountsWhatItSkipped()
    {
        using var storage = new TestStorage();
        var (_, library) = await SetupAsync(storage);

        var result = await library.SyncWithDiskAsync();

        Assert.Equal(new SyncResult(1, 2, 3), result);
        // The sync and the list agree afterwards too.
        Assert.Equal(3, (await library.FindLooseFilesAsync()).Count(f => f.IsImage));
    }

    [Fact]
    public async Task DeletingRemovesOnlyLooseFilesAndEmptyFolders()
    {
        using var storage = new TestStorage();
        var (_, library) = await SetupAsync(storage);

        var (files, _) = await library.DeleteLooseFilesAsync(["Hall/Flowers/old/deep.jpeg", "Kitchen/Herbs/Rose.jpeg", "Hall/Flowers/Rose.jpeg", "Hall/Flowers/Tulip.jpeg", "../outside.jpeg"]);

        Assert.Equal(1, files);
        Assert.False(File.Exists(storage.Upload("Hall/Flowers/old/deep.jpeg")));
        Assert.False(Directory.Exists(storage.Upload("Hall/Flowers/old")));
        // An image's file, and files the sync would add, stay.
        Assert.True(File.Exists(storage.Upload("Hall/Flowers/Rose.jpeg")));
        Assert.True(File.Exists(storage.Upload("Hall/Flowers/Tulip.jpeg")));
        Assert.True(File.Exists(storage.Upload("Kitchen/Herbs/Rose.jpeg")));
        Assert.True(Directory.Exists(storage.Paths.UploadsPath));
    }

    [Fact]
    public async Task DeletingTheLastFileKeepsTheUploadsFolder()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("stray.jpeg");

        await Library(storage, db).DeleteLooseFilesAsync(["stray.jpeg"]);

        Assert.True(Directory.Exists(storage.Paths.UploadsPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(storage.Paths.UploadsPath));
    }
}
