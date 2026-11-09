using System.Security.Cryptography;
using System.Text;

namespace MeuralImageBackup.Core.Services;

// The token MeuralManager (or anything else) sends in an X-API-Key header to read the library
// through the photo source API. Generated on the Settings page and shown once; only its SHA-256
// hash is kept, so a copy of the database can't be used to call the API. Without a token the API
// turns every request away. Like MeuralManager's Homepage widget token.
public sealed class ApiTokenService(SettingsService settings)
{
    public const string HashKey = "ApiTokenHash";
    public const string CreatedKey = "ApiTokenCreatedAt";

    // The hash is read once and kept until the token changes: the API checks it on every request,
    // and a grid of thumbnails is many requests.
    private string? _hash;
    private bool _loaded;

    public async Task<bool> HasTokenAsync(CancellationToken ct = default) => await HashAsync(ct) is not null;

    // When the current token was made (UTC), or null without one.
    public async Task<DateTime?> CreatedAtAsync(CancellationToken ct = default) =>
        DateTime.TryParse(await settings.GetAsync(CreatedKey, ct), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;

    // Makes a new token (the old one stops working) and returns it; it can't be read back later.
    public async Task<string> GenerateAsync(CancellationToken ct = default)
    {
        var token = "mib_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var hash = Hash(token);
        await settings.SetAsync(HashKey, hash, ct);
        await settings.SetAsync(CreatedKey, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture), ct);
        (_hash, _loaded) = (hash, true);
        return token;
    }

    public async Task RevokeAsync(CancellationToken ct = default)
    {
        await settings.SetAsync(HashKey, null, ct);
        await settings.SetAsync(CreatedKey, null, ct);
        (_hash, _loaded) = (null, true);
    }

    public async Task<bool> IsValidAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || await HashAsync(ct) is not { } stored)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token.Trim())), Encoding.ASCII.GetBytes(stored));
    }

    private async Task<string?> HashAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            _hash = await settings.GetAsync(HashKey, ct);
            _loaded = true;
        }
        return _hash;
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
