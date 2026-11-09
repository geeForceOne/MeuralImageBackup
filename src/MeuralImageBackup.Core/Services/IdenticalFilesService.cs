using System.Collections.Concurrent;
using System.Security.Cryptography;
using MeuralImageBackup.Core.Files;

namespace MeuralImageBackup.Core.Services;

// One image in a group of identical files: its copies (one per gallery folder that has the file).
public sealed record IdenticalImage(int Id, string Name, int FileId, int AccessCount, DateTime CreatedAt, DateTime LastAccessedAt, IReadOnlyList<ImageView> Copies);

// Different images (different Meural ids, any names) whose files are byte for byte the same. The
// first image is the one "merge all" keeps: the most shown, then the oldest.
public sealed record IdenticalFiles(string Hash, long Size, IReadOnlyList<IdenticalImage> Images)
{
    public IdenticalImage Keeper => Images[0];

    // The space the other images' files take.
    public long Wasted => Images.Skip(1).Sum(i => i.Copies.Count) * Size;
}

// Finds images whose files are identical. Only files that share their size with another file are
// read, so a collection of JPEGs (whose sizes rarely match unless they're the same) costs little.
// Fingerprints are kept by path, size and time, so a second look is quick.
public sealed class IdenticalFilesService(ImagePaths paths)
{
    private readonly ConcurrentDictionary<string, (long Size, long Version, string Hash)> _hashes = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<IdenticalFiles>> FindAsync(Library library, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Each file once (a gallery showing another gallery's copy doesn't count).
        var files = library.Images.Where(v => v.HasOwnCopy)
            .GroupBy(v => v.RelativePath, StringComparer.Ordinal)
            .Select(g => (Path: g.Key, Size: g.First().Size, Version: g.First().Version, Views: g.ToList()))
            .ToList();
        var candidates = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToList();

        var hashed = new List<(string Hash, long Size, List<ImageView> Views)>();
        for (var n = 0; n < candidates.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            var file = candidates[n];
            if (n % 20 == 0)
                progress?.Report($"Comparing files: {n:N0} of {candidates.Count:N0}");
            if (await HashAsync(file.Path, file.Size, file.Version, ct) is { } hash)
                hashed.Add((hash, file.Size, file.Views));
        }

        return hashed
            .GroupBy(h => h.Hash, StringComparer.Ordinal)
            .Select(g => new IdenticalFiles(g.Key, g.First().Size, g
                .SelectMany(h => h.Views)
                .GroupBy(v => v.Id)
                .Select(i => new IdenticalImage(i.Key, i.First().Name, i.First().FileId, i.First().AccessCount,
                    i.First().CreatedAt, i.First().LastAccessedAt,
                    i.OrderBy(v => v.GalleryName, StringComparer.CurrentCultureIgnoreCase).ToList()))
                .OrderByDescending(i => i.AccessCount)
                .ThenBy(i => i.CreatedAt)
                .ThenBy(i => i.Id)
                .ToList()))
            .Where(g => g.Images.Count > 1)
            .OrderByDescending(g => g.Wasted)
            .ThenBy(g => g.Keeper.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<string?> HashAsync(string relative, long size, long version, CancellationToken ct)
    {
        if (_hashes.TryGetValue(relative, out var known) && known.Size == size && known.Version == version)
            return known.Hash;
        if (paths.FullPath(relative) is not { } full)
            return null;
        try
        {
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            _hashes[relative] = (size, version, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
