using System.Net;
using MeuralImageBackup.Core;
using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Files;
using Microsoft.EntityFrameworkCore;

namespace MeuralImageBackup.Tests;

// A throwaway uploads folder and database in the temp folder, deleted afterwards.
public sealed class TestStorage : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "mib-tests-" + Guid.NewGuid().ToString("N")[..10]);
    public StoragePaths Paths { get; }

    public TestStorage()
    {
        Paths = StoragePaths.From(Path.Combine(Root, "uploads"), Path.Combine(Root, "database"), Path.Combine(Root, "thumbs"), Root);
        Paths.CreateDirectories();
    }

    public ImagePaths ImagePaths(bool useFrameFolder = true) => new(Paths, useFrameFolder);

    public DbFactory Db() => new(Paths.ConnectionString);

    public async Task<DbFactory> MigratedDbAsync()
    {
        var factory = Db();
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return factory;
    }

    public string Upload(string relative) => Path.Combine(Paths.UploadsPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteUpload(string relative, string content = "jpeg")
    {
        var path = Upload(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class DbFactory(string connectionString) : IDbContextFactory<MeuralDb>
{
    public MeuralDb CreateDbContext() => new(new DbContextOptionsBuilder<MeuralDb>().UseSqlite(connectionString).Options);
}

// Answers every request with the given status and body, and counts them.
public sealed class FakeImageServer(HttpStatusCode status = HttpStatusCode.OK, string body = "jpeg-bytes") : HttpMessageHandler
{
    public int Requests { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
