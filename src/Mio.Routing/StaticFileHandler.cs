namespace Mio.Routing;

/// <summary>Resolves entry HTML and script paths from the static file root.</summary>
public sealed class StaticFileHandler(string rootPath)
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public string? Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(RootPath, relativePath.TrimStart('/')));
        return full.StartsWith(RootPath) && File.Exists(full) ? full : null;
    }

    public byte[]? Read(string relativePath)
    {
        var path = Resolve(relativePath);
        return path != null ? File.ReadAllBytes(path) : null;
    }

    public string? ReadText(string relativePath)
    {
        var path = Resolve(relativePath);
        return path != null ? File.ReadAllText(path) : null;
    }
}
