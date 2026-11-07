using MeuralImageBackup.Core;
using Markdig;

namespace MeuralImageBackup.Web.Services;

// The release notes page's content: the repository's CHANGELOG.md, embedded into the app at build
// time (see the .csproj) so a running instance shows the notes up to its own version, with no
// network access. Rendered once; raw HTML in the file is escaped, not passed through.
public static class ReleaseNotes
{
    private static readonly Lazy<string> RenderedHtml = new(Render);

    public static string Html => RenderedHtml.Value;

    private static string Render()
    {
        using var stream = typeof(ReleaseNotes).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null)
            return "<p>No release notes are included in this build.</p>";
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().ReplaceLineEndings("\n");
        // The file's own "# Release notes" title; the page has its heading already.
        if (text.StartsWith("# "))
            text = text[(text.IndexOf('\n') + 1)..];
        // A version not released yet shows the running version's build date instead.
        if (AppInfo.BuildDate is { } date)
            text = text.Replace(" - unreleased", " - " + date);
        var pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();
        return Markdown.ToHtml(text, pipeline);
    }
}
