using System.Globalization;
using MeuralImageBackup.Core;
using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Web.Api;
using MeuralImageBackup.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// MeuralImageUploader's places, relative to the working directory (/app in the container): the
// images in uploads/, the database in database/. Each can be moved with Storage__UploadsPath,
// Storage__DatabaseDirectory and Storage__ThumbnailsPath (local development uses ./data).
var storage = builder.Configuration.GetSection("Storage");
var paths = StoragePaths.From(storage["UploadsPath"], storage["DatabaseDirectory"], storage["ThumbnailsPath"], builder.Environment.ContentRootPath);
paths.CreateDirectories();

// SKIP_DUPLICATE_DOWNLOADS=true: an image stored in one gallery's folder isn't downloaded again into
// another gallery's. Off unless set, so the folders keep MeuralImageUploader's layout.
var skipStoredImages = MeuralImageBackup.Core.Services.UploadOptions.ParseSkipStoredImages(builder.Configuration["SKIP_DUPLICATE_DOWNLOADS"]);
builder.Services.AddMeuralCore(paths, ImagePathsSetting(builder.Configuration["USE_FRAME_NAME_IN_PATH"]), skipStoredImages);

builder.Services.AddDataProtection()
    .SetApplicationName("MeuralImageBackup")
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysPath));

// The UI is in US English; numbers and dates follow the browser's region format, de-CH (the old
// app's fixed format) when the browser doesn't say.
builder.Services.Configure<RequestLocalizationOptions>(o =>
{
    o.SupportedCultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures).ToList();
    o.SupportedUICultures = [CultureInfo.GetCultureInfo("en-US")];
    o.DefaultRequestCulture = new RequestCulture("de-CH", "en-US");
    o.FallBackToParentCultures = true;
    o.RequestCultureProviders = [new AcceptLanguageHeaderRequestCultureProvider()];
});

builder.Services.AddSingleton<PhotoSourceLibrary>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(o => o.DetailedErrors = builder.Environment.IsDevelopment());

var app = builder.Build();

// Behind a reverse proxy that ends HTTPS, links keep https.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// An existing meural.db from MeuralImageUploader already has its two migrations; a new one is created.
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MeuralDb>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
app.Logger.LogInformation("{App} {Version}: images in {Uploads}, database {Database}, frame folders {FrameFolders}, duplicate downloads {Duplicates}",
    AppInfo.Name, AppInfo.Version, paths.UploadsPath, paths.DatabaseFile,
    ImagePathsSetting(app.Configuration["USE_FRAME_NAME_IN_PATH"]) ? "on" : "off",
    skipStoredImages ? "skipped" : "kept");

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseRequestLocalization();

// The downloaded images at /Images/uploads/..., as before. Only the uploads folder: the old app
// served all of /app there, including the database.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(paths.UploadsPath),
    RequestPath = "/Images/uploads",
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "public, max-age=3600",
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapCompatibleApi();
app.MapImageEndpoints();
app.MapPhotoSourceApi();

app.Run();

static bool ImagePathsSetting(string? value) => MeuralImageBackup.Core.Files.ImagePaths.ParseUseFrameFolder(value);

// For WebApplicationFactory in the tests.
public partial class Program;
