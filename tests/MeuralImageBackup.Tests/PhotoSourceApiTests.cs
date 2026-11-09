using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeuralImageBackup.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeuralImageBackup.Tests;

// The photo source API for MeuralManager: Immich's shapes, as MeuralManager's ImmichApiClient reads
// them, behind the token from the Settings page. Part of ApiTests, which shares one app (the app
// reads its paths from process-wide environment variables).
public partial class ApiTests
{
    private async Task<HttpClient> ClientWithTokenAsync()
    {
        var token = await _app.Services.GetRequiredService<ApiTokenService>().GenerateAsync();
        var client = _app.CreateClient();
        client.DefaultRequestHeaders.Add("x-api-key", token);
        return client;
    }

    private async Task ShowAsync(string gallery, int id, string title)
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/api/meural",
            new { frameName = "Source", gallery, title, url = $"https://x/{id}.jpg", id });
        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task ThePhotoSourceNeedsTheToken(string? key)
    {
        await _app.Services.GetRequiredService<ApiTokenService>().GenerateAsync();
        var client = _app.CreateClient();
        if (key is not null)
            client.DefaultRequestHeaders.Add("X-API-Key", key);

        var response = await client.GetAsync("/api/albums");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ARevokedTokenStopsWorking()
    {
        var client = await ClientWithTokenAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        await _app.Services.GetRequiredService<ApiTokenService>().RevokeAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task ARegeneratedTokenReplacesTheOldOne()
    {
        var old = await ClientWithTokenAsync();
        var current = await ClientWithTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/users/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await current.GetAsync("/api/users/me")).StatusCode);
    }

    [Fact]
    public async Task ThePhotoSourceAnswersLikeImmich()
    {
        await ShowAsync("Seascapes", 9001, "Wave");
        await ShowAsync("Seascapes", 9002, "Harbor at Dawn");
        await ShowAsync("Portraits", 9003, "Girl");
        // Shown from a second gallery as well: an image there too, so a second asset.
        await ShowAsync("Portraits", 9001, "Wave");
        var client = await ClientWithTokenAsync();

        // Connection test: no email, so MeuralManager shows the name.
        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me");
        Assert.Equal("MeuralImageBackup", me.GetProperty("name").GetString());

        // Albums are the galleries.
        var albums = await client.GetFromJsonAsync<JsonElement>("/api/albums");
        var seascapes = albums.EnumerateArray().Single(a => a.GetProperty("albumName").GetString()!.StartsWith("Seascapes", StringComparison.Ordinal));
        Assert.Equal(2, seascapes.GetProperty("assetCount").GetInt32());
        Assert.False(string.IsNullOrEmpty(seascapes.GetProperty("albumThumbnailAssetId").GetString()));
        var seascapesId = seascapes.GetProperty("id").GetString();

        // Metadata search: an album's images, paged, newest first, with nextPage as a string.
        var first = await SearchAsync(client, "metadata", new { type = "IMAGE", order = "desc", page = 1, size = 1, albumIds = new[] { seascapesId } });
        var items = first.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("2", first.GetProperty("nextPage").GetString());
        var asset = items[0];
        Assert.Equal("Harbor at Dawn.jpeg", asset.GetProperty("originalFileName").GetString());
        Assert.Equal("image/jpeg", asset.GetProperty("originalMimeType").GetString());
        Assert.Equal("IMAGE", asset.GetProperty("type").GetString());
        Assert.True(asset.GetProperty("localDateTime").TryGetDateTimeOffset(out _));
        Assert.True(asset.GetProperty("fileCreatedAt").TryGetDateTimeOffset(out _));

        var second = await SearchAsync(client, "metadata", new { type = "IMAGE", page = 2, size = 1, albumIds = new[] { seascapesId } });
        Assert.Equal("Wave.jpeg", second.GetProperty("items")[0].GetProperty("originalFileName").GetString());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextPage").ValueKind);

        // The same Meural file in two galleries is two images, so two assets.
        var all = await SearchAsync(client, "metadata", new { type = "IMAGE", size = 1000 });
        Assert.Equal(2, all.GetProperty("items").EnumerateArray().Count(a => a.GetProperty("originalFileName").GetString() == "Wave.jpeg"));

        // Smart search is a text search on names, image and gallery.
        var byName = await SearchAsync(client, "smart", new { query = "harbor dawn", size = 100 });
        Assert.Equal(["Harbor at Dawn.jpeg"], byName.GetProperty("items").EnumerateArray().Select(a => a.GetProperty("originalFileName").GetString()));
        var byGallery = await SearchAsync(client, "smart", new { query = "portraits", size = 100 });
        Assert.Equal(2, byGallery.GetProperty("items").GetArrayLength());

        // There are no people.
        var people = await SearchAsync(client, "metadata", new { personIds = new[] { "someone" } });
        Assert.Equal(0, people.GetProperty("items").GetArrayLength());

        // The image itself: preview, fullsize and original are the file.
        var id = asset.GetProperty("id").GetString();
        Assert.Equal("jpeg-bytes", await client.GetStringAsync($"/api/assets/{id}/original"));
        var preview = await client.GetAsync($"/api/assets/{id}/thumbnail?size=preview");
        Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
        Assert.Equal("jpeg-bytes", await preview.Content.ReadAsStringAsync());

        var missing = await client.GetAsync("/api/assets/999999/original");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<JsonElement> SearchAsync(HttpClient client, string kind, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/search/{kind}", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assets");
    }
}
