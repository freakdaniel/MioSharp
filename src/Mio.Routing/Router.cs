namespace Mio.Routing;

/// <summary>
/// In-process route table — no HTTP server, no sockets, no IPC.
/// JS fetch() calls are intercepted and dispatched here synchronously.
/// </summary>
public sealed class Router
{
    private readonly List<Route> _routes = [];
    private string? _staticFilesRoot;

    public void MapGet(string path, Func<RouteContext, RouteResponse> handler) =>
        _routes.Add(new Route("GET", path, handler));

    public void MapPost(string path, Func<RouteContext, RouteResponse> handler) =>
        _routes.Add(new Route("POST", path, handler));

    public void MapGet(string path, Func<RouteContext, Task<RouteResponse>> handler) =>
        _routes.Add(new Route("GET", path, ctx => handler(ctx).GetAwaiter().GetResult()));

    public void MapPost(string path, Func<RouteContext, Task<RouteResponse>> handler) =>
        _routes.Add(new Route("POST", path, ctx => handler(ctx).GetAwaiter().GetResult()));

    public void UseStaticFiles(string rootPath) => _staticFilesRoot = rootPath;

    public RouteResponse Dispatch(string method, string url, byte[]? body = null)
    {
        var uri = new Uri("http://mio" + url);
        var path = uri.AbsolutePath;

        // Query string
        var ctx = new RouteContext(method, path);
        if (!string.IsNullOrEmpty(uri.Query))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq >= 0) ctx.Query[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        ctx.Body = body;

        // Named routes first
        foreach (var route in _routes)
        {
            if (route.Method == ctx.Method && Matches(route.Pattern, path))
                return route.Handler(ctx);
        }

        // Static files fallback
        if (_staticFilesRoot != null)
        {
            var filePath = Path.GetFullPath(Path.Combine(_staticFilesRoot, path.TrimStart('/')));
            if (!filePath.StartsWith(Path.GetFullPath(_staticFilesRoot))) // path traversal guard
                return new RouteResponse(403, "text/plain", "Forbidden"u8.ToArray());

            if (File.Exists(filePath))
                return new RouteResponse(200, GetMimeType(filePath), File.ReadAllBytes(filePath));

            // SPA fallback: return index.html for unknown paths
            var index = Path.Combine(_staticFilesRoot, "index.html");
            if (File.Exists(index))
                return new RouteResponse(200, "text/html", File.ReadAllBytes(index));
        }

        return new RouteResponse(404, "text/plain", "Not Found"u8.ToArray());
    }

    private static bool Matches(string pattern, string path) =>
        string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase);

    private static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" or ".mjs" => "application/javascript",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };

    private sealed record Route(string Method, string Pattern, Func<RouteContext, RouteResponse> Handler);
}
