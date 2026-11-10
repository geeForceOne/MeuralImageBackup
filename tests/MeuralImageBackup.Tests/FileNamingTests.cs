using MeuralImageBackup.Core;
using MeuralImageBackup.Core.Files;

namespace MeuralImageBackup.Tests;

// The names must come out exactly as MeuralImageUploader made them, or existing files aren't found.
public class FileNamingTests
{
    [Theory]
    [InlineData("Impressionists", "Impressionists")]
    [InlineData("Art: \"Best\" <of> 2023?", "Art Best of 2023")]
    [InlineData("a/b\\c|d*e", "abcde")]
    [InlineData("Ünïcödé & spaces.", "Ünïcödé & spaces.")]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("???", "")]
    public void FolderNames(string? name, string expected) => Assert.Equal(expected, FileNaming.FolderName(name));

    [Fact]
    public void NoFrameFolder() => Assert.Equal("", FileNaming.FolderName("Living room", use: false));

    [Fact]
    public void ControlCharactersAreDropped() => Assert.Equal("ab", FileNaming.FolderName("a\tb\n"));

    [Theory]
    [InlineData("Water Lilies", 42, "Water Lilies")]
    [InlineData(null, 42, "42")]
    [InlineData("", 42, "42")]
    [InlineData(null, null, "Unknown")]
    [InlineData("Who? Me:", null, "Who Me")]
    public void FileNames(string? title, int? fileId, string expected) => Assert.Equal(expected, FileNaming.FileName(title, fileId));

    [Fact]
    public void PathsWithAndWithoutTheFrameFolder()
    {
        using var storage = new TestStorage();
        Assert.Equal("Living room/Impressionists/Water Lilies.jpeg", storage.ImagePaths(true).File("Living room", "Impressionists", "Water Lilies", 1));
        Assert.Equal("Impressionists/Water Lilies.jpeg", storage.ImagePaths(false).File("Living room", "Impressionists", "Water Lilies", 1));
        Assert.Equal("Unknown/Unknown/7.jpeg", storage.ImagePaths(true).File(null, null, null, 7));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    [InlineData("no", true)]
    [InlineData("0", true)]
    public void UseFrameNameInPath(string? value, bool expected) => Assert.Equal(expected, ImagePaths.ParseUseFrameFolder(value));

    [Fact]
    public void NamesThatLeaveTheUploadsFolderAreRefused()
    {
        using var storage = new TestStorage();
        var paths = storage.ImagePaths(false);
        Assert.Null(paths.FullPath(paths.File("frame", "..", "x", 1)));
        Assert.NotNull(paths.FullPath(paths.File("frame", "ok", "..", 1)));
    }

    [Fact]
    public void UrlsEscapeEachPart() =>
        Assert.Equal("/Images/uploads/Living%20room/A%20%26%20B/Caf%C3%A9%20%231.jpeg", ImagePaths.Url("Living room/A & B/Café #1.jpeg"));

    [Theory]
    [InlineData("2.0.0", "2.0.0.0")]
    [InlineData("2.1", "2.1.0.0")]
    [InlineData("1.1.1.0", "1.1.1.0")]
    [InlineData("dev", "0.0.0.0")]
    [InlineData("dev-abc1234", "0.0.0.0")]
    public void ApiVersionHasFourParts(string version, string expected) => Assert.Equal(expected, AppInfo.FourPartVersion(version));

    [Theory]
    [InlineData(null, "2.0.0", null, true)]
    [InlineData("2.0.0", "2.0.0", null, false)]
    [InlineData("1.9.0", "2.0.0", "2.0.0", false)]
    [InlineData("2.5.0", "2.0.0", "2.0.0", false)]
    [InlineData("garbage", "2.0.0", "2.0.0", false)]
    [InlineData(null, "dev", null, false)]
    public void WhatsNewBanner(string? seen, string running, string? show, bool store) =>
        Assert.Equal((show, store), WhatsNew.Decide(seen, running));
}
