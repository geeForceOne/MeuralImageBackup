namespace MeuralImageBackup.Core.Models;

// One app setting, by key. There are no users, so settings are for the whole installation; they
// live in meural.db so they're in the database volume. Added in 2.2.0 (migration 004_Settings).
public class Setting
{
    public string Key { get; set; } = "";
    public string? Value { get; set; }
}
