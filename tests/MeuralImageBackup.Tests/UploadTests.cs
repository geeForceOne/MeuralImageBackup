using System.Net;
using MeuralImageBackup.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// /api/meural's behavior, as MeuralImageUploader had it.
public class UploadTests
{
    private static UploadService Service(TestStorage storage, DbFactory db, HttpMessageHandler server, bool useFrameFolder = true) =>
        new(db, storage.ImagePaths(useFrameFolder), new HttpClient(server), NullLogger<UploadService>.Instance);

    private static MeuralUpload Shown(string gallery, int id, string? frame = "Living room", string? title = null) =>
        new(frame, title ?? $"Image {id}", gallery, $"https://images.example/{id}.jpg", id);

    [Fact]
    public async Task SavesTheImageAndRecordsIt()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();

        await Service(storage, db, server).HandleAsync(Shown("Impressionists", 1, title: "Water Lilies"));

        Assert.Equal("jpeg-bytes", File.ReadAllText(storage.Upload("Living room/Impressionists/Water Lilies.jpeg")));
        await using var ctx = db.CreateDbContext();
        var gallery = await ctx.Galleries.Include(g => g.Images).SingleAsync();
        Assert.Equal(("Living room", "Impressionists", 1), (gallery.FrameName, gallery.Name, gallery.AccessCount));
        Assert.Equal(("Water Lilies", 1, 1), (gallery.Images![0].Name, gallery.Images[0].FileId, gallery.Images[0].AccessCount));
    }

    [Fact]
    public async Task AGalleryCountsOnlyWhenTheFrameSwitchesToIt()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var service = Service(storage, db, new FakeImageServer());

        await service.HandleAsync(Shown("A", 1));
        await service.HandleAsync(Shown("A", 2));
        await service.HandleAsync(Shown("A", 3));
        await service.HandleAsync(Shown("B", 4));
        await service.HandleAsync(Shown("A", 1));
        // Another frame doesn't interfere with this one's last gallery.
        await service.HandleAsync(Shown("A", 5, frame: "Office"));
        await service.HandleAsync(Shown("A", 2));

        await using var ctx = db.CreateDbContext();
        var counts = await ctx.Galleries.ToDictionaryAsync(g => (g.FrameName, g.Name), g => g.AccessCount);
        Assert.Equal(2, counts[("Living room", "A")]);
        Assert.Equal(1, counts[("Living room", "B")]);
        Assert.Equal(1, counts[("Office", "A")]);
        Assert.Equal(2, await ctx.LastGalleries.CountAsync());
    }

    [Fact]
    public async Task AnImageIsOneEntryPerFileIdAndCountsEveryReport()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();
        var service = Service(storage, db, server);

        await service.HandleAsync(Shown("A", 1));
        await service.HandleAsync(Shown("A", 1));
        await service.HandleAsync(Shown("B", 1));

        await using var ctx = db.CreateDbContext();
        var image = await ctx.Images.Include(i => i.Gallery).SingleAsync();
        Assert.Equal(3, image.AccessCount);
        Assert.Equal("A", image.Gallery!.Name);
        // Downloaded once into A, once into B (the file is per gallery folder), not again for A.
        Assert.Equal(2, server.Requests);
        Assert.True(File.Exists(storage.Upload("Living room/B/Image 1.jpeg")));
    }

    [Fact]
    public async Task WithoutATitleTheFileIsNamedAfterTheMeuralId()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();

        await Service(storage, db, new FakeImageServer()).HandleAsync(new MeuralUpload("Frame", null, "G", "https://x/1.jpg", 987));

        Assert.True(File.Exists(storage.Upload("Frame/G/987.jpeg")));
        await using var ctx = db.CreateDbContext();
        Assert.Equal("987", (await ctx.Images.SingleAsync()).Name);

        var library = await new LibraryService(db, storage.ImagePaths(), NullLogger<LibraryService>.Instance).LoadAsync();
        Assert.True(library.Images.Single().Exists);
    }

    [Fact]
    public async Task WithoutTheFrameFolder()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();

        await Service(storage, db, new FakeImageServer(), useFrameFolder: false).HandleAsync(Shown("G", 1, title: "T"));

        Assert.True(File.Exists(storage.Upload("G/T.jpeg")));
    }

    [Fact]
    public async Task WithoutAFrameNameTheImageIsSavedButNotRecorded()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();

        await Service(storage, db, new FakeImageServer()).HandleAsync(Shown("G", 1, frame: null, title: "T"));

        Assert.True(File.Exists(storage.Upload("Unknown/G/T.jpeg")));
        await using var ctx = db.CreateDbContext();
        Assert.Equal(0, await ctx.Galleries.CountAsync());
        Assert.Equal(0, await ctx.Images.CountAsync());
    }

    [Fact]
    public async Task AnExistingFileIsNotDownloadedAgain()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        storage.WriteUpload("Living room/G/Image 1.jpeg", "original");
        var server = new FakeImageServer();

        await Service(storage, db, server).HandleAsync(Shown("G", 1));

        Assert.Equal(0, server.Requests);
        Assert.Equal("original", File.ReadAllText(storage.Upload("Living room/G/Image 1.jpeg")));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "<html>not found</html>")]
    [InlineData(HttpStatusCode.OK, "")]
    public async Task AFailedOrEmptyDownloadLeavesNoFile(HttpStatusCode status, string body)
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();

        await Service(storage, db, new FakeImageServer(status, body)).HandleAsync(Shown("G", 1));

        Assert.Empty(Directory.GetFiles(storage.Paths.UploadsPath, "*", SearchOption.AllDirectories));
        // Recorded all the same, as before.
        await using var ctx = db.CreateDbContext();
        Assert.Equal(1, await ctx.Images.CountAsync());
    }

    [Fact]
    public async Task AGalleryNamedDotDotIsNotWrittenOutsideTheUploads()
    {
        using var storage = new TestStorage();
        var db = await storage.MigratedDbAsync();
        var server = new FakeImageServer();

        await Service(storage, db, server, useFrameFolder: false).HandleAsync(Shown("..", 1, title: "evil"));

        Assert.Equal(0, server.Requests);
        Assert.False(File.Exists(Path.Combine(storage.Root, "evil.jpeg")));
    }
}
