using MeuralImageBackup.Core;
using MeuralImageBackup.Core.Services;

namespace MeuralImageBackup.Web.Api;

// MeuralImageUploader's API, which Home Assistant calls. Its routes, body and answers must not
// change (see "Compatibility" in CLAUDE.md). No authentication, as before. Route matching is
// case-insensitive, so /api/Meural works too.
public static class CompatibleApi
{
    public static void MapCompatibleApi(this IEndpointRouteBuilder app)
    {
        // The body binds like the old [FromBody] MeuralMeta: JSON with any property casing, every
        // property optional, the id also as a string ("123"). Always 200 with an empty body, even when
        // the download fails; problems go to the log.
        app.MapPost("/api/meural", async (MeuralUpload upload, UploadService uploads, CancellationToken ct) =>
        {
            await uploads.HandleAsync(upload, ct);
            return Results.Ok();
        });

        // The four-part version as plain text, e.g. "2.0.0.0".
        app.MapGet("/api/version", () => Results.Text(AppInfo.ApiVersion, "text/plain"));
    }
}
