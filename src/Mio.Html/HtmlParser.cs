using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Mio.Html;

/// <summary>
/// Parses HTML5 strings and files into an AngleSharp IDocument (live DOM).
/// </summary>
public sealed class HtmlParser
{
    private readonly IBrowsingContext _context;
    private readonly IHtmlParser _parser;

    public HtmlParser()
    {
        var config = Configuration.Default;
        _context = BrowsingContext.New(config);
        _parser = _context.GetService<IHtmlParser>()
            ?? new AngleSharp.Html.Parser.HtmlParser();
    }

    /// <summary>Parses an HTML string and returns the DOM document.</summary>
    public async Task<IDocument> ParseAsync(string html, CancellationToken ct = default)
    {
        return await _context.OpenAsync(req => req.Content(html), ct);
    }

    /// <summary>Parses an HTML string synchronously.</summary>
    public IDocument Parse(string html)
    {
        return _parser.ParseDocument(html);
    }

    /// <summary>Parses an HTML file from disk.</summary>
    public async Task<IDocument> ParseFileAsync(string path, CancellationToken ct = default)
    {
        var html = await File.ReadAllTextAsync(path, ct);
        return await ParseAsync(html, ct);
    }
}
