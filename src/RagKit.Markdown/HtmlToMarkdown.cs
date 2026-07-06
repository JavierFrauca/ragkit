namespace RagKit.Markdown;

/// <summary>HTML → Markdown via ReverseMarkdown (HtmlAgilityPack under the hood).</summary>
public static class HtmlToMarkdown
{
    public static string Convert(string path) => ConvertHtml(File.ReadAllText(path));

    public static string ConvertHtml(string html)
    {
        var config = new ReverseMarkdown.Config { GithubFlavored = true }; // tables, strikethrough, etc.
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.PassThrough;
        config.Formatting.RemoveComments = true;
        config.Links.SmartHref = true;
        var converter = new ReverseMarkdown.Converter(config);
        return converter.Convert(html).Trim();
    }
}
