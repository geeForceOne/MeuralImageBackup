using MeuralImageBackup.Core.Services;
using MeuralImageBackup.Web.Services;

namespace MeuralImageBackup.Tests;

// The table headers' sorting: each sort has a natural direction, "dir" reverses it.
public class SortTests
{
    private static ImageView Image(string name, int shown) =>
        new(shown, shown, name, 1, "Gallery", "Frame", DateTime.UtcNow, DateTime.UtcNow.AddDays(-shown), shown, $"Frame/Gallery/{name}.jpeg", true, 1000, 0);

    private static readonly ImageView[] Images = [Image("Banana", 2), Image("apple", 5), Image("Cherry", 1)];

    [Theory]
    [InlineData("name", null, "apple,Banana,Cherry")]
    [InlineData("name", "desc", "Cherry,Banana,apple")]
    [InlineData("shown", null, "apple,Banana,Cherry")]
    [InlineData("shown", "asc", "Cherry,Banana,apple")]
    [InlineData("recent", null, "Cherry,Banana,apple")]
    [InlineData("unknown", null, "Cherry,Banana,apple")]
    public void SortsBothWays(string sort, string? dir, string expected) =>
        Assert.Equal(expected, string.Join(",", Filters.Sort(Images, sort, dir).Select(i => i.Name)));

    [Theory]
    [InlineData("name", false, null)]
    [InlineData("name", true, "desc")]
    [InlineData("size", true, null)]
    [InlineData("size", false, "asc")]
    public void DirIsOnlyWrittenAgainstTheNaturalDirection(string sort, bool descending, string? dir) =>
        Assert.Equal(dir, Filters.Dir(sort, descending));
}
