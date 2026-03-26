using AngleSharp.Dom;
using Mio.Core;
using Mio.Css;
using Mio.Html;
using Mio.Layout;
using Mio.Platform;
using Mio.Rendering;
using Mio.Routing;
using Mio.Script;
using SkiaSharp;

namespace Mio.App;

/// <summary>
/// The top-level application host. Wires together all engine subsystems.
/// </summary>
public sealed class MioApplication : IDisposable
{
    private readonly WindowOptions _windowOptions;
    private readonly Router _router;
    private string? _entryPath;
    private string? _staticFilesRoot;

    private MioPlatformWindow? _window;
    private readonly HtmlParser _htmlParser = new();
    private readonly StyleEngine _styleEngine = new();
    private LayoutEngine? _layoutEngine;
    private readonly Painter _painter = new();
    private ScriptEngine? _scriptEngine;

    private IDocument? _document;
    private LayoutBox? _layoutRoot;
    private bool _dirtyLayout = true;
    private DateTime _startTime = DateTime.UtcNow;

    /// <summary>Route registration — call before Run().</summary>
    public Router Router => _router;

    /// <summary>Script engine — available after Run() has initialized.</summary>
    public ScriptEngine Script => _scriptEngine ?? throw new InvalidOperationException("Call Run() first.");

    public static MioApplicationBuilder CreateBuilder(string[]? args = null) => MioApplicationBuilder.Create(args);

    internal MioApplication(WindowOptions windowOptions, Router router, string? entryPath, string? staticFilesRoot)
    {
        _windowOptions = windowOptions;
        _router = router;
        _entryPath = entryPath;
        _staticFilesRoot = staticFilesRoot;
    }

    public MioApplication UseStaticFiles(string root)
    {
        _staticFilesRoot = root;
        _router.UseStaticFiles(root);
        return this;
    }

    public MioApplication MapGet(string path, Func<RouteContext, RouteResponse> handler)
    {
        _router.MapGet(path, handler);
        return this;
    }

    public MioApplication MapPost(string path, Func<RouteContext, RouteResponse> handler)
    {
        _router.MapPost(path, handler);
        return this;
    }

    public MioApplication LoadEntry(string htmlPath)
    {
        _entryPath = htmlPath;
        return this;
    }

    public void Run()
    {
        _layoutEngine = new LayoutEngine(_styleEngine);
        _scriptEngine = new ScriptEngine(_router);

        _window = new MioPlatformWindow(_windowOptions);
        _window.Render += OnRender;
        _window.Resized += (_, e) => { _dirtyLayout = true; };
        _window.Closed += (_, _) => { };

        // Load entry HTML
        if (_entryPath != null)
            LoadHtml(_entryPath);

        _window.Run();
    }

    private void LoadHtml(string path)
    {
        string html;

        // Check if it's a route or a file path
        if (path.StartsWith('/') && _staticFilesRoot != null)
        {
            var response = _router.Dispatch("GET", path);
            html = response.BodyText;
        }
        else if (File.Exists(path))
        {
            html = File.ReadAllText(path);
        }
        else
        {
            html = $"<html><body><h1>File not found: {path}</h1></body></html>";
        }

        _document = _htmlParser.Parse(html);
        _scriptEngine!.SetDocument(_document);
        _scriptEngine.DomChanged += () => _dirtyLayout = true;

        // Execute <script> tags
        foreach (var script in _document.QuerySelectorAll("script"))
        {
            var src = script.GetAttribute("src");
            if (src != null)
            {
                // Load script file
                string? scriptContent = null;
                if (_staticFilesRoot != null)
                {
                    var response = _router.Dispatch("GET", src);
                    if (response.Status == 200)
                        scriptContent = response.BodyText;
                }
                if (scriptContent != null)
                    _scriptEngine.Execute(scriptContent, src);
            }
            else if (!string.IsNullOrWhiteSpace(script.TextContent))
            {
                _scriptEngine.Execute(script.TextContent!, "inline");
            }
        }

        _dirtyLayout = true;
    }

    private void OnRender(SKCanvas canvas, Size size)
    {
        // Fire pending JS timers
        _scriptEngine?.Tick(DateTime.UtcNow - _startTime);

        if (_dirtyLayout && _document != null && _layoutEngine != null)
        {
            _layoutRoot = _layoutEngine.LayoutDocument(_document, size);
            _dirtyLayout = false;
        }

        if (_layoutRoot != null)
            _painter.Paint(canvas, _layoutRoot);
    }

    public void Dispose()
    {
        _scriptEngine?.Dispose();
        _window?.Dispose();
    }
}
