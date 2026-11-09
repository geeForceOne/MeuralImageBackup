using MeuralImageBackup.Core.Data;
using MeuralImageBackup.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace MeuralImageBackup.Core.Services;

// The installation's settings (the Settings table), by key.
public sealed class SettingsService(IDbContextFactory<MeuralDb> dbFactory)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);
    }

    // Null removes the setting.
    public async Task SetAsync(string key, string? value, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (value is null)
        {
            if (setting is not null)
                db.Settings.Remove(setting);
        }
        else if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}
