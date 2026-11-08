using MeuralImageBackup.Core.Services;

namespace MeuralImageBackup.Web.Api;

public static class ImageEndpoints
{
    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        // An image's thumbnail for the grids, from its copy in gallery g (?g=) when given. The URL
        // carries the file's version (?v=), so browsers can keep it for good.
        app.MapGet("/thumbs/{imageId:int}", async (int imageId, int? g, LibraryService library, ThumbnailService thumbnails, HttpContext http, CancellationToken ct) =>
        {
            var original = await library.FilePathAsync(imageId, g, ct);
            var thumb = original is null ? null : await thumbnails.GetAsync(original, ct);
            if (thumb is null)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(thumb, "image/jpeg");
        });
    }
}
