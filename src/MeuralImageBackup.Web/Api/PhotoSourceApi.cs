using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Metadata;
using MeuralImageBackup.Core;
using MeuralImageBackup.Core.Services;

namespace MeuralImageBackup.Web.Api;

// The library as a photo source for MeuralManager, next to Immich. It answers the part of Immich's
// REST API that MeuralManager's ImmichApiClient uses, in the same shapes, so MeuralManager can talk
// to it with a second instance of that client: users/me, search/metadata, search/smart, albums,
// assets/{id}/thumbnail and assets/{id}/original. Immich's people aren't offered.
//
// - An asset is a recorded image whose file exists; its id is the image's id. The same Meural file
//   in two galleries is two images, so two assets. Its date is when the backup first saved it.
// - An album is a gallery, with the images in it that have a file.
// - search/smart is a plain text search on the image's and its gallery's names (Immich's is an AI
//   image search, which this app doesn't have).
//
// Every request needs the token from the Settings page in an X-API-Key header (Immich's header);
// without a token set, every request is turned away. Read only.
public static class PhotoSourceApi
{
    private const int MaxPageSize = 1000;

    public static void MapPhotoSourceApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var tokens = http.RequestServices.GetRequiredService<ApiTokenService>();
                if (!await tokens.IsValidAsync(http.Request.Headers["X-API-Key"].ToString(), http.RequestAborted))
                    return Results.Json(new ErrorBody("Invalid API key", 401), statusCode: StatusCodes.Status401Unauthorized);
                return await next(context);
            })
            .WithMetadata(new SkipStatusCodePages());

        // Who the key belongs to; MeuralManager's connection test shows the email, else the name.
        api.MapGet("/users/me", () => Results.Json(new UserBody("meuralimagebackup", AppInfo.Name)));

        api.MapPost("/search/metadata", async (SearchBody? body, PhotoSourceLibrary source, CancellationToken ct) =>
            Results.Json(Search(await source.GetAsync(ct), body ?? new SearchBody(), text: null)));

        api.MapPost("/search/smart", async (SearchBody? body, PhotoSourceLibrary source, CancellationToken ct) =>
            Results.Json(Search(await source.GetAsync(ct), body ?? new SearchBody(), text: body?.Query)));

        api.MapGet("/albums", async (PhotoSourceLibrary source, CancellationToken ct) =>
        {
            var library = await source.GetAsync(ct);
            var severalFrames = library.Frames.Count > 1;
            return Results.Json(library.Galleries
                .OrderByDescending(g => g.LastAccessedAt)
                .Select(g =>
                {
                    var shown = g.Images.Where(i => i.Exists).ToList();
                    return new AlbumBody(
                        Id: Id(g.Id),
                        AlbumName: severalFrames ? $"{g.Name} · {g.FrameName}" : g.Name,
                        Description: g.FrameName,
                        AssetCount: shown.Count,
                        AlbumThumbnailAssetId: shown.Count > 0 ? Id(shown[0].Id) : null,
                        CreatedAt: g.CreatedAt,
                        UpdatedAt: g.LastAccessedAt,
                        StartDate: shown.Count > 0 ? shown.Min(i => i.CreatedAt) : null,
                        EndDate: shown.Count > 0 ? shown.Max(i => i.CreatedAt) : null);
                })
                .ToList());
        });

        // "thumbnail" is the app's own thumbnail (longest side 480 px); "preview" and "fullsize"
        // are the original, which is a JPEG already.
        api.MapGet("/assets/{id}/thumbnail", async (string id, string? size, LibraryService library, ThumbnailService thumbnails, CancellationToken ct) =>
        {
            if (await FileAsync(library, id, ct) is not { } original)
                return NotFound();
            if (size is "preview" or "fullsize")
                return Results.File(original, "image/jpeg");
            var thumb = await thumbnails.GetAsync(original, ct);
            return thumb is null ? NotFound() : Results.File(thumb, "image/jpeg");
        });

        api.MapGet("/assets/{id}/original", async (string id, LibraryService library, CancellationToken ct) =>
            await FileAsync(library, id, ct) is { } original
                ? Results.File(original, "image/jpeg", Path.GetFileName(original))
                : NotFound());
    }

    // One page of assets, newest first unless the body asks for "asc". Page numbers start at 1;
    // nextPage is the following page as a string, or null after the last (as Immich does).
    internal static SearchResultBody Search(Library library, SearchBody body, string? text)
    {
        var size = Math.Clamp(body.Size ?? 250, 1, MaxPageSize);
        var page = Math.Max(body.Page ?? 1, 1);
        var albums = body.AlbumIds?.Select(ParseId).ToHashSet();

        IEnumerable<IGrouping<int, ImageView>> images = library.Images.Where(i => i.Exists).GroupBy(i => i.Id);
        // Only images, and there are no people here.
        if (body.Type is { } type && !type.Equals("IMAGE", StringComparison.OrdinalIgnoreCase) || body.PersonIds is { Length: > 0 })
            images = [];
        if (albums is not null)
            images = images.Where(g => albums.All(a => g.Any(v => v.GalleryId == a)));
        if (body.TakenAfter is { } after)
            images = images.Where(g => g.First().CreatedAt >= after.UtcDateTime);
        if (body.TakenBefore is { } before)
            images = images.Where(g => g.First().CreatedAt <= before.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            images = images.Where(g => words.All(w =>
                g.First().Name.Contains(w, StringComparison.CurrentCultureIgnoreCase) ||
                g.Any(v => v.GalleryName.Contains(w, StringComparison.CurrentCultureIgnoreCase))));
        }

        var ordered = string.Equals(body.Order, "asc", StringComparison.OrdinalIgnoreCase)
            ? images.OrderBy(g => g.First().CreatedAt).ThenBy(g => g.Key)
            : images.OrderByDescending(g => g.First().CreatedAt).ThenByDescending(g => g.Key);
        var all = ordered.ToList();
        var items = all.Skip((page - 1) * size).Take(size).Select(g => Asset(g.First(v => v.Exists))).ToList();
        var next = page * size < all.Count ? (page + 1).ToString(CultureInfo.InvariantCulture) : null;
        return new SearchResultBody(
            new SearchAssetsBody(all.Count, items.Count, items, next),
            new SearchAlbumsBody(0, 0, [], []));
    }

    private static AssetBody Asset(ImageView image) => new(
        Id: Id(image.Id),
        Type: "IMAGE",
        OriginalFileName: Path.GetFileName(image.RelativePath),
        OriginalMimeType: "image/jpeg",
        LocalDateTime: image.CreatedAt,
        FileCreatedAt: image.CreatedAt,
        FileModifiedAt: image.CreatedAt,
        UpdatedAt: image.LastAccessedAt,
        IsFavorite: false,
        IsArchived: false,
        IsTrashed: false,
        Visibility: "timeline",
        // Not Immich's: what the backup knows besides.
        Title: image.Name,
        MeuralId: image.FileId,
        TimesShown: image.AccessCount,
        LastShownAt: image.LastAccessedAt);

    private static async Task<string?> FileAsync(LibraryService library, string id, CancellationToken ct) =>
        ParseId(id) is var imageId && imageId > 0 ? await library.FilePathAsync(imageId, ct) : null;

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static int ParseId(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : -1;

    private static IResult NotFound() => Results.Json(new ErrorBody("Not found", 404), statusCode: StatusCodes.Status404NotFound);

    // The request body of search/metadata and search/smart; anything else Immich takes is ignored.
    public sealed record SearchBody(
        string? Type = null, string? Order = null, int? Page = null, int? Size = null,
        DateTimeOffset? TakenAfter = null, DateTimeOffset? TakenBefore = null,
        string[]? AlbumIds = null, string[]? PersonIds = null, string? Query = null);

    public sealed record SearchResultBody(SearchAssetsBody Assets, SearchAlbumsBody Albums);
    public sealed record SearchAssetsBody(int Total, int Count, IReadOnlyList<AssetBody> Items, string? NextPage);
    public sealed record SearchAlbumsBody(int Total, int Count, IReadOnlyList<AlbumBody> Items, IReadOnlyList<object> Facets);

    public sealed record AssetBody(
        string Id, string Type, string OriginalFileName, string OriginalMimeType,
        DateTime LocalDateTime, DateTime FileCreatedAt, DateTime FileModifiedAt, DateTime UpdatedAt,
        bool IsFavorite, bool IsArchived, bool IsTrashed, string Visibility,
        string Title, int MeuralId, int TimesShown, DateTime LastShownAt);

    public sealed record AlbumBody(
        string Id, string AlbumName, string Description, int AssetCount, string? AlbumThumbnailAssetId,
        DateTime CreatedAt, DateTime UpdatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? StartDate,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? EndDate);

    // No email (there are no accounts), so MeuralManager shows the name.
    public sealed record UserBody(string Id, string Name);

    public sealed record ErrorBody(string Message, int StatusCode);
}

// The library for the photo source API, kept for a short while: MeuralManager pages through
// searches and loads albums in quick succession, and each full load walks the uploads folder.
public sealed class PhotoSourceLibrary(LibraryService library)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (Library Library, DateTime At)? _cached;

    public async Task<Library> GetAsync(CancellationToken ct)
    {
        if (_cached is { } c && DateTime.UtcNow - c.At < MaxAge)
            return c.Library;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { } again && DateTime.UtcNow - again.At < MaxAge)
                return again.Library;
            var loaded = await library.LoadAsync(ct);
            _cached = (loaded, DateTime.UtcNow);
            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }
}

// Keeps the status code pages (the "not found" page) away from API answers: a client gets the JSON
// error, not an HTML page.
public sealed class SkipStatusCodePages : ISkipStatusCodePagesMetadata;
