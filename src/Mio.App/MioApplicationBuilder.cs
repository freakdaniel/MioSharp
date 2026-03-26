using Mio.Core;
using Mio.Routing;

namespace Mio.App;

public sealed class MioApplicationBuilder
{
    private readonly WindowOptions _windowOptions = new();
    private readonly Router _router = new();
    private string? _entryPath;
    private string? _staticFilesRoot;

    public static MioApplicationBuilder Create(string[]? args = null) => new();

    public MioApplicationBuilder WithTitle(string title)
    {
        _windowOptions.Title = title;
        return this;
    }

    public MioApplicationBuilder WithSize(float width, float height)
    {
        _windowOptions.Size = new Size(width, height);
        return this;
    }

    public MioApplicationBuilder WithSize(Size size)
    {
        _windowOptions.Size = size;
        return this;
    }

    public MioApplicationBuilder Resizable(bool resizable = true)
    {
        _windowOptions.Resizable = resizable;
        return this;
    }

    public MioApplication Build() =>
        new(_windowOptions, _router, _entryPath, _staticFilesRoot);

    // Allow builder-level route registration before Build()
    internal Router Router => _router;
    internal string? EntryPath { get => _entryPath; set => _entryPath = value; }
    internal string? StaticFilesRoot { get => _staticFilesRoot; set => _staticFilesRoot = value; }
}
