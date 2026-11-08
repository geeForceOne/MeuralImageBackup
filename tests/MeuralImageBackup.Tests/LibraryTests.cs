using MeuralImageBackup.Core.Models;
using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

public class LibraryTests
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

    [Fact]
    public async Task KnowsWhichFilesAreThere()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "F", "G", (1, "here"), (2, "gone"));
        storage.WriteUpload("F/G/here.jpeg", "12345");

        var library = await Library(storage, db).LoadAsync();

        var here = library.Images.Single(i => i.Name == "here");
        Assert.True(here.Exists);
        Assert.Equal(5, here.Size);
        Assert.False(library.Images.Single(i => i.Name == "gone").Exists);
        Assert.Equal(1, library.Galleries.Single().Missing);
    }

    [Fact]
    public async Task DeletingAGalleryDeletesItsFolder()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var id = await AddGalleryAsync(db, "F", "G", (1, "a"));
        storage.WriteUpload("F/G/a.jpeg");
        storage.WriteUpload("F/G/not recorded.jpeg");
        storage.WriteUpload("F/Other/b.jpeg");

        await Library(storage, db).DeleteGalleryAsync(id);

        Assert.False(Directory.Exists(storage.Upload("F/G")));
        Assert.True(File.Exists(storage.Upload("F/Other/b.jpeg")));
        await using var ctx = db.CreateDbContext();
        Assert.Equal(0, await ctx.Images.CountAsync());
    }

    [Fact]
    public async Task ASharedFolderKeepsTheOtherGallerysFiles()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var mine = await AddGalleryAsync(db, "Living room", "G", (1, "mine"));
        await AddGalleryAsync(db, "Office", "G", (2, "theirs"));
        storage.WriteUpload("G/mine.jpeg");
        storage.WriteUpload("G/theirs.jpeg");

        await Library(storage, db, useFrameFolder: false).DeleteGalleryAsync(mine);

        Assert.False(File.Exists(storage.Upload("G/mine.jpeg")));
        Assert.True(File.Exists(storage.Upload("G/theirs.jpeg")));
    }

    [Fact]
    public async Task DeletingAnImageDeletesItsFile()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "F", "G", (1, "a"), (2, "b"));
        storage.WriteUpload("F/G/a.jpeg");
        storage.WriteUpload("F/G/b.jpeg");
        var library = Library(storage, db);
        var a = (await library.LoadAsync()).Images.Single(i => i.Name == "a");

        await library.DeleteImageAsync(a.Id, a.GalleryId);

        Assert.False(File.Exists(storage.Upload("F/G/a.jpeg")));
        Assert.True(File.Exists(storage.Upload("F/G/b.jpeg")));
        Assert.Single((await library.LoadAsync()).Images);
    }

    [Fact]
    public async Task RemovesTheEntriesWithoutAFile()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        await AddGalleryAsync(db, "F", "G", (1, "here"), (2, "gone"), (3, "also gone"));
        storage.WriteUpload("F/G/here.jpeg");
        var library = Library(storage, db);

        Assert.Equal(2, await library.RemoveMissingAsync());
        Assert.Equal("here", (await library.LoadAsync()).Images.Single().Name);
    }
}
