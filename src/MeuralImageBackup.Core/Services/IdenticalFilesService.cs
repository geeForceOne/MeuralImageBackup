using System.Collections.Concurrent;
using System.Security.Cryptography;
using MeuralImageBackup.Core.Files;

namespace MeuralImageBackup.Core.Services;

// One image in a group of identical files. Copies is its view (one: an image has one file).
public sealed record IdenticalImage(int Id, string Name, int FileId, int AccessCount, DateTime CreatedAt, DateTime LastAccessedAt, IReadOnlyList<ImageView> Copies);

// Different images (any Meural ids, any names, any galleries) whose files are byte for byte the
// same: the same artwork uploaded twice, or the same Meural file shown in two galleries. The
// first image is the one "keep one of each" keeps: the most shown, then the oldest.
// Two images with the same title in the same gallery share one file (files are named after the
// title, so the second was never downloaded): removing one removes only its entry.
public sealed record IdenticalFiles(string Hash, long Size, IReadOnlyList<IdenticalImage> Images)
{
    public IdenticalImage Keeper => Images[0];

    // How many files there are on disk, and how many of them keeping the given image deletes (the
    // others' files that aren't also that image's).
    public int Files => Images.SelectMany(i => i.Copies).Select(c => c.RelativePath).Distinct(StringComparer.Ordinal).Count();

    public int ExtraFiles(IdenticalImage keep)
    {
        var kept = keep.Copies.Select(c => c.RelativePath).ToHashSet(StringComparer.Ordinal);
        return Images.Where(i => i.Id != keep.Id).SelectMany(i => i.Copies)
            .Select(c => c.RelativePath).Where(p => !kept.Contains(p)).Distinct(StringComparer.Ordinal).Count();
    }

    // The space keeping the first image frees.
    public long Wasted => ExtraFiles(Keeper) * Size;

    // All the images use one and the same file.
    public bool SharedFile => Files == 1;
}

// Finds images whose files are identical. Only files that share their size with another file are
// read, so a collection of JPEGs (whose sizes rarely match unless they're the same) costs little.
// Fingerprints are kept by path, size and time, so a second look is quick.
public sealed class IdenticalFilesService(ImagePaths paths)
{
    private readonly ConcurrentDictionary<string, (long Size, long Version, string Hash)> _hashes = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<IdenticalFiles>> FindAsync(Library library, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Each file once (two images with the same title in a folder two galleries share use one).
        var files = library.Images.Where(v => v.Exists)
            .GroupBy(v => v.RelativePath, StringComparer.Ordinal)
            .Select(g => (Path: g.Key, Size: g.First().Size, Version: g.First().Version, Views: g.ToList()))
            .ToList();
        // Files that share their size with another file, and files that several images use (same
        // title in the same gallery: one file, so no other file of that size to compare with).
        var candidates = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g)
            .Concat(files.Where(f => f.Views.Select(v => v.Id).Distinct().Count() > 1))
            .DistinctBy(f => f.Path, StringComparer.Ordinal)
            .ToList();

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
