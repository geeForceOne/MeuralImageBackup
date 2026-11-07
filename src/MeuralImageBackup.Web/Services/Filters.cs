using MeuralImageBackup.Core.Services;

namespace MeuralImageBackup.Web.Services;

// The filters and sort orders the Galleries and Images pages share. Kept in the URL, so going back
// to a page shows it as it was.
public static class Filters
{
    // "Not shown for": the choices, in days.
    public static readonly (int? Days, string Label)[] Ages =
        [(null, "Any time"), (7, "a week"), (30, "30 days"), (90, "90 days"), (180, "180 days"), (365, "a year")];

    public static readonly (string Key, string Label)[] GallerySorts =
        [("recent", "Recently shown"), ("shown", "Most shown"), ("name", "Name"), ("images", "Most images"), ("frame", "Frame"), ("added", "Newest"), ("size", "Largest")];

    public static readonly (string Key, string Label)[] ImageSorts =
        [("recent", "Recently shown"), ("shown", "Most shown"), ("name", "Name"), ("gallery", "Gallery"), ("added", "Newest"), ("size", "Largest")];

    public static bool Matches(string? search, params string[] texts) =>
        string.IsNullOrWhiteSpace(search) || texts.Any(t => t.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // Not shown for at least this many days (null: any time).
    public static bool OlderThan(DateTime lastShownUtc, int? days) =>
        days is null || lastShownUtc < DateTime.UtcNow.AddDays(-days.Value);

    public static IEnumerable<GalleryView> Sort(IEnumerable<GalleryView> galleries, string? sort) => sort switch
    {
        "shown" => galleries.OrderByDescending(g => g.AccessCount).ThenByDescending(g => g.LastAccessedAt),
        "name" => galleries.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.FrameName),
        "images" => galleries.OrderByDescending(g => g.Images.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        "frame" => galleries.OrderBy(g => g.FrameName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
        "added" => galleries.OrderByDescending(g => g.CreatedAt),
        "size" => galleries.OrderByDescending(g => g.Size),
        _ => galleries.OrderByDescending(g => g.LastAccessedAt),
    };

    public static IEnumerable<ImageView> Sort(IEnumerable<ImageView> images, string? sort) => sort switch
    {
        "shown" => images.OrderByDescending(i => i.AccessCount).ThenByDescending(i => i.LastAccessedAt),
        "name" => images.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        "gallery" => images.OrderBy(i => i.GalleryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        "added" => images.OrderByDescending(i => i.CreatedAt),
        "size" => images.OrderByDescending(i => i.Size),
        _ => images.OrderByDescending(i => i.LastAccessedAt),
    };
}
