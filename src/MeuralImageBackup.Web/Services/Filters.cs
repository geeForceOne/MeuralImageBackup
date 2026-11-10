using MeuralImageBackup.Core.Services;

namespace MeuralImageBackup.Web.Services;

// The filters and sort orders the pages share. Kept in the URL, so going back to a page shows it as
// it was; the sort (and the view) is also remembered per browser (SortPref).
public static class Filters
{
    // "Not shown for": the choices, in days.
    public static readonly (int? Days, string Label)[] Ages =
        [(null, "Any time"), (7, "a week"), (30, "30 days"), (90, "90 days"), (180, "180 days"), (365, "a year")];

    // Each sort has a natural direction (names A to Z, numbers and dates largest first); the
    // dropdown labels describe that one, and "dir" in the URL reverses it.
    public static readonly (string Key, string Label)[] GallerySorts =
        [("recent", "Recently shown"), ("shown", "Most shown"), ("name", "Name"), ("images", "Most images"), ("frame", "Frame"), ("added", "Newest"), ("size", "Largest")];

    public static readonly (string Key, string Label)[] ImageSorts =
        [("recent", "Recently shown"), ("shown", "Most shown"), ("name", "Name"), ("gallery", "Gallery"), ("frame", "Frame"), ("added", "Newest"), ("size", "Largest")];

    public const string DefaultSort = "recent";

    // Sorts whose natural order is A to Z; the others go largest or newest first.
    private static readonly HashSet<string> Ascending = ["name", "gallery", "frame"];

    public static bool NaturallyDescending(string sort) => !Ascending.Contains(sort);

    // Whether a sort runs descending, given the URL's "dir" (asc, desc or none for the natural one).
    public static bool IsDescending(string sort, string? dir) => dir switch
    {
        "asc" => false,
        "desc" => true,
        _ => NaturallyDescending(sort),
    };

    // The "dir" to put in the URL: null when it's the sort's natural direction.
    public static string? Dir(string sort, bool descending) =>
        descending == NaturallyDescending(sort) ? null : descending ? "desc" : "asc";

    public static bool Matches(string? search, params string[] texts) =>
        string.IsNullOrWhiteSpace(search) || texts.Any(t => t.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // Not shown for at least this many days (null: any time).
    public static bool OlderThan(DateTime lastShownUtc, int? days) =>
        days is null || lastShownUtc < DateTime.UtcNow.AddDays(-days.Value);

    public static IEnumerable<GalleryView> Sort(IEnumerable<GalleryView> galleries, string? sort, string? dir = null)
    {
        var key = Known(sort, GallerySorts);
        IEnumerable<GalleryView> natural = key switch
        {
            "shown" => galleries.OrderByDescending(g => g.AccessCount).ThenByDescending(g => g.LastAccessedAt),
            "name" => galleries.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.FrameName),
            "images" => galleries.OrderByDescending(g => g.Images.Count).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "frame" => galleries.OrderBy(g => g.FrameName, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase),
            "added" => galleries.OrderByDescending(g => g.CreatedAt),
            "size" => galleries.OrderByDescending(g => g.Size),
            _ => galleries.OrderByDescending(g => g.LastAccessedAt),
        };
        return IsDescending(key, dir) == NaturallyDescending(key) ? natural : natural.Reverse();
    }

    public static IEnumerable<ImageView> Sort(IEnumerable<ImageView> images, string? sort, string? dir = null)
    {
        var key = Known(sort, ImageSorts);
        IEnumerable<ImageView> natural = key switch
        {
            "shown" => images.OrderByDescending(i => i.AccessCount).ThenByDescending(i => i.LastAccessedAt),
            "name" => images.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "gallery" => images.OrderBy(i => i.GalleryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "frame" => images.OrderBy(i => i.FrameName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.GalleryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            "added" => images.OrderByDescending(i => i.CreatedAt),
            "size" => images.OrderByDescending(i => i.Size),
            _ => images.OrderByDescending(i => i.LastAccessedAt),
        };
        return IsDescending(key, dir) == NaturallyDescending(key) ? natural : natural.Reverse();
    }

    // The sort key if it's one of the choices, else the default.
    public static string Known(string? sort, (string Key, string Label)[] choices) =>
        sort is not null && choices.Any(c => c.Key == sort) ? sort : DefaultSort;
}
