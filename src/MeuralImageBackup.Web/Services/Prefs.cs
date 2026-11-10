using Microsoft.JSInterop;

namespace MeuralImageBackup.Web.Services;

// Per-browser preferences (meural.getPref / setPref: localStorage with the "mib." prefix). Blocked
// storage or a closed connection falls back to the default instead of breaking the page.
public sealed class Prefs(IJSRuntime js)
{
    public async Task<string?> GetAsync(string key)
    {
        try { return await js.InvokeAsync<string?>("meural.getPref", key); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { return null; }
    }

    public async Task SetAsync(string key, string value)
    {
        try { await js.InvokeVoidAsync("meural.setPref", key, value); }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException) { }
    }

    // A sort is remembered as "key" or "key:asc" / "key:desc" when it runs against its natural direction.
    public async Task<(string? Sort, string? Dir)> GetSortAsync(string key)
    {
        var value = await GetAsync(key);
        if (string.IsNullOrEmpty(value)) return (null, null);
        var parts = value.Split(':', 2);
        return (parts[0], parts.Length > 1 && parts[1] is "asc" or "desc" ? parts[1] : null);
    }

    public Task SetSortAsync(string key, string sort, string? dir) =>
        SetAsync(key, dir is null ? sort : $"{sort}:{dir}");
}
