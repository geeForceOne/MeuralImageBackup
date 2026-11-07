using System.Net;
using System.Net.Http.Json;
using System.Text;
using MeuralImageBackup.Core.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeuralImageBackup.Tests;

// An existing installation must keep working after swapping the image: its database, its files
// and Home Assistant's calls. See "Compatibility" in CLAUDE.md.
public class CompatibilityTests
{
    // meural.db as MeuralImageUploader 1.1.1 (EF Core 7) left it.
    private static void CreateOldDatabase(string file)
    {
        using var connection = new SqliteConnection($"Data Source={file}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY, "ProductVersion" TEXT NOT NULL);
            CREATE TABLE "Galleries" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Galleries" PRIMARY KEY AUTOINCREMENT, "FrameName" TEXT NOT NULL, "Name" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "LastAccessedAt" TEXT NOT NULL, "AccessCount" INTEGER NOT NULL);
            CREATE TABLE "LastGalleries" ("Id" INTEGER NOT NULL CONSTRAINT "PK_LastGalleries" PRIMARY KEY AUTOINCREMENT, "Name" TEXT NOT NULL, "FrameName" TEXT NOT NULL);
            CREATE TABLE "Images" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Images" PRIMARY KEY AUTOINCREMENT, "GalleryId" INTEGER NOT NULL, "FileId" INTEGER NOT NULL, "Name" TEXT NOT NULL, "CreatedAt" TEXT NOT NULL, "LastAccessedAt" TEXT NOT NULL, "AccessCount" INTEGER NOT NULL, CONSTRAINT "FK_Images_Galleries_GalleryId" FOREIGN KEY ("GalleryId") REFERENCES "Galleries" ("Id") ON DELETE CASCADE);
            CREATE INDEX "IX_Gallery_Name" ON "Galleries" ("Name");
            CREATE INDEX "IX_Image_FileId" ON "Images" ("FileId");
            CREATE INDEX "IX_Image_Name" ON "Images" ("Name");
            CREATE INDEX "IX_Images_GalleryId" ON "Images" ("GalleryId");
            CREATE INDEX "IX_LastGallery_FrameName" ON "LastGalleries" ("FrameName");
            INSERT INTO "__EFMigrationsHistory" VALUES ('20230305214800__001_Initial', '7.0.3'), ('20230320163505_002_LastAccessFix', '7.0.4');
            INSERT INTO "Galleries" VALUES (1, 'Living room', 'Impressionists', '2023-03-17 10:00:00.1234567', '2023-03-20 16:35:05.7654321', 4);
            INSERT INTO "Images" VALUES (1, 1, 555, 'Water Lilies', '2023-03-17 10:00:01', '2023-03-20 16:35:06', 9);
            INSERT INTO "LastGalleries" VALUES (1, 'Impressionists', 'Living room');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task AnOldDatabaseIsTakenAsItIs()
    {
        using var storage = new TestStorage();
        CreateOldDatabase(storage.Paths.DatabaseFile);
        storage.WriteUpload("Living room/Impressionists/Water Lilies.jpeg");
        var db = storage.Db();

        await using (var ctx = db.CreateDbContext())
        {
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            await ctx.Database.MigrateAsync();
        }

        var library = await new LibraryService(db, storage.ImagePaths(), NullLogger<LibraryService>.Instance).LoadAsync();
        var gallery = Assert.Single(library.Galleries);
        Assert.Equal(("Impressionists", 4, 1), (gallery.Name, gallery.AccessCount, gallery.Images.Count));
        Assert.Equal(new DateTime(2023, 3, 20, 16, 35, 5, DateTimeKind.Utc), gallery.LastAccessedAt.AddTicks(-7654321));
        var image = gallery.Images.Single();
        Assert.True(image.Exists);
        Assert.Equal(9, image.AccessCount);

        // The same gallery still counts on: the frame's last gallery was kept.
        var uploads = new UploadService(db, storage.ImagePaths(), new HttpClient(new FakeImageServer()), NullLogger<UploadService>.Instance);
        await uploads.HandleAsync(new MeuralUpload("Living room", "Water Lilies", "Impressionists", "https://x/555.jpg", 555));
        await using var check = db.CreateDbContext();
        Assert.Equal(4, (await check.Galleries.SingleAsync()).AccessCount);
        Assert.Equal(10, (await check.Images.SingleAsync()).AccessCount);
    }
}

// The HTTP side, through the real app.
public class ApiTests : IClassFixture<ApiTests.App>
{
    public sealed class App : WebApplicationFactory<Program>
    {
        public TestStorage Storage { get; } = new();
        public FakeImageServer ImageServer { get; } = new();

        public App()
        {
            // Program reads these before the host is built, so they're set as environment variables.
            Environment.SetEnvironmentVariable("Storage__UploadsPath", Storage.Paths.UploadsPath);
            Environment.SetEnvironmentVariable("Storage__DatabaseDirectory", Storage.Paths.DatabaseDirectory);
            Environment.SetEnvironmentVariable("Storage__ThumbnailsPath", Storage.Paths.ThumbnailsPath);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
                services.AddHttpClient<UploadService>().ConfigurePrimaryHttpMessageHandler(() => ImageServer));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
                Storage.Dispose();
        }
    }

    private readonly App _app;

    public ApiTests(App app) => _app = app;

    [Fact]
    public async Task PostMeuralAnswers200WithAnEmptyBody()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/meural", new { frameName = "Hall", gallery = "Dutch", title = "Night Watch", url = "https://x/1.jpg", id = 1 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.True(File.Exists(_app.Storage.Upload("Hall/Dutch/Night Watch.jpeg")));
    }

    [Fact]
    public async Task PostMeuralTakesAnyCasingAndAnIdAsString()
    {
        var client = _app.CreateClient();
        var json = """{"FrameName":"Hall","GALLERY":"Dutch","Title":"Milkmaid","Url":"https://x/2.jpg","Id":"2"}""";

        var response = await client.PostAsync("/api/Meural", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(File.Exists(_app.Storage.Upload("Hall/Dutch/Milkmaid.jpeg")));
    }

    [Fact]
    public async Task PostMeuralAnswers200EvenWithoutAUsableUrl()
    {
        var client = _app.CreateClient();

        var response = await client.PostAsJsonAsync("/api/meural", new { frameName = "Hall", gallery = "Dutch", title = "Broken" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task VersionIsPlainTextWithFourParts()
    {
        var response = await _app.CreateClient().GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ImagesAreServedUnderImagesUploads()
    {
        _app.Storage.WriteUpload("Hall/My Gallery/Café.jpeg", "picture");
        var client = _app.CreateClient();

        Assert.Equal("picture", await client.GetStringAsync("/Images/uploads/Hall/My%20Gallery/Caf%C3%A9.jpeg"));
        // MeuralImageUploader's links escaped only the file name.
        Assert.Equal("picture", await client.GetStringAsync("/Images/uploads/Hall/My Gallery/Caf%C3%A9.jpeg"));
    }

    [Fact]
    public async Task TheDatabaseIsNotServed()
    {
        var client = _app.CreateClient();
        await client.GetAsync("/api/version");

        var response = await client.GetAsync("/Images/database/meural.db");

        Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("SQLite format", await response.Content.ReadAsStringAsync());
    }
}
