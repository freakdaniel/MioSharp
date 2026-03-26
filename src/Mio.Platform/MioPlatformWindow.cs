using Mio.Core;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using SilkKey = Silk.NET.Input.Key;
using SilkMouseButton = Silk.NET.Input.MouseButton;
using Silk.NET.Input;

namespace Mio.Platform;

/// <summary>
/// Cross-platform native window built on Silk.NET (GLFW backend) with a
/// GPU-accelerated SkiaSharp surface backed by an OpenGL context.
/// Works on Windows, macOS, and Linux without any platform-specific code.
/// </summary>
public sealed class MioPlatformWindow : IWindowSurface, IDisposable
{
    private IWindow? _window;
    private GL? _gl;
    private GRContext? _grContext;
    private SKSurface? _skiaSurface;
    private GRBackendRenderTarget? _renderTarget;
    private IInputContext? _input;
    private bool _disposed;

    public Size Size { get; private set; }
    public float DevicePixelRatio { get; private set; } = 1f;

    public event EventHandler<ResizeEventArgs>? Resized;
    public event EventHandler<MouseEventArgs>? MouseDown;
    public event EventHandler<MouseEventArgs>? MouseUp;
    public event EventHandler<MouseMoveEventArgs>? MouseMove;
    public event EventHandler<ScrollEventArgs>? Scroll;
    public event EventHandler<KeyEventArgs>? KeyDown;
    public event EventHandler<KeyEventArgs>? KeyUp;
    public event EventHandler<TextInputEventArgs>? TextInput;
    public event EventHandler? Closed;

    /// <summary>Called each frame; the engine paints into the provided canvas.</summary>
    public event Action<SKCanvas, Size>? Render;

    private readonly Core.WindowOptions _options;

    public MioPlatformWindow(Core.WindowOptions options)
    {
        _options = options;
        Size = options.Size;
    }

    public void Run()
    {
        var silkOpts = Silk.NET.Windowing.WindowOptions.Default with
        {
            Title = _options.Title,
            Size = new Vector2D<int>((int)_options.Size.Width, (int)_options.Size.Height),
            WindowBorder = _options.Resizable ? WindowBorder.Resizable : WindowBorder.Fixed,
            API = GraphicsAPI.Default, // OpenGL
            ShouldSwapAutomatically = false,
            IsVisible = true,
        };

        _window = Window.Create(silkOpts);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Resize += OnResize;
        _window.Closing += OnClosing;
        _window.Run();
    }

    private void OnLoad()
    {
        _gl = _window!.CreateOpenGL();
        _input = _window.CreateInput();

        foreach (var kb in _input.Keyboards)
        {
            kb.KeyDown += OnKeyDown;
            kb.KeyUp += OnKeyUp;
            kb.KeyChar += OnKeyChar;
        }
        foreach (var mouse in _input.Mice)
        {
            mouse.MouseDown += OnMouseDown;
            mouse.MouseUp += OnMouseUp;
            mouse.MouseMove += OnMouseMove;
            mouse.Scroll += OnScroll;
        }

        // Create SkiaSharp GRContext from the current OpenGL context
        var glInterface = GRGlInterface.CreateOpenGl(proc => _window.GLContext!.TryGetProcAddress(proc, out var p) ? p : 0);
        _grContext = GRContext.CreateGl(glInterface);

        CreateSkiaSurface((int)Size.Width, (int)Size.Height);
    }

    private void OnRender(double _dt)
    {
        if (_skiaSurface == null || _grContext == null) return;

        var canvas = _skiaSurface.Canvas;
        canvas.Clear(SKColors.White);

        Render?.Invoke(canvas, Size);

        canvas.Flush();
        _grContext.Flush();

        _window!.SwapBuffers();
    }

    private void OnResize(Vector2D<int> size)
    {
        Size = new Core.Size(size.X, size.Y);
        _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        CreateSkiaSurface(size.X, size.Y);
        Resized?.Invoke(this, new ResizeEventArgs(Size));
    }

    private void OnClosing()
    {
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CreateSkiaSurface(int w, int h)
    {
        _skiaSurface?.Dispose();
        _renderTarget?.Dispose();

        // Wrap the default framebuffer (id=0) as a SkiaSharp render target
        var fbInfo = new GRGlFramebufferInfo(0, 0x8058); // GL_RGBA8
        _renderTarget = new GRBackendRenderTarget(w, h, sampleCount: 0, stencilBits: 8, fbInfo);
        _skiaSurface = SKSurface.Create(
            _grContext,
            _renderTarget,
            GRSurfaceOrigin.BottomLeft,
            SKColorType.Rgba8888);
    }

    private void OnMouseDown(IMouse m, SilkMouseButton b) =>
        MouseDown?.Invoke(this, new MouseEventArgs(ToPoint(m.Position), Map(b)));
    private void OnMouseUp(IMouse m, SilkMouseButton b) =>
        MouseUp?.Invoke(this, new MouseEventArgs(ToPoint(m.Position), Map(b)));
    private void OnMouseMove(IMouse m, System.Numerics.Vector2 pos) =>
        MouseMove?.Invoke(this, new MouseMoveEventArgs(new Point(pos.X, pos.Y)));
    private void OnScroll(IMouse m, ScrollWheel sw) =>
        Scroll?.Invoke(this, new ScrollEventArgs(ToPoint(m.Position), sw.X, sw.Y));
    private void OnKeyDown(IKeyboard kb, SilkKey key, int sc) =>
        KeyDown?.Invoke(this, new KeyEventArgs(key.ToString(), $"Key{key}",
            kb.IsKeyPressed(SilkKey.ControlLeft) || kb.IsKeyPressed(SilkKey.ControlRight),
            kb.IsKeyPressed(SilkKey.AltLeft) || kb.IsKeyPressed(SilkKey.AltRight),
            kb.IsKeyPressed(SilkKey.ShiftLeft) || kb.IsKeyPressed(SilkKey.ShiftRight)));
    private void OnKeyUp(IKeyboard kb, SilkKey key, int sc) =>
        KeyUp?.Invoke(this, new KeyEventArgs(key.ToString(), $"Key{key}", false, false, false));
    private void OnKeyChar(IKeyboard kb, char c) =>
        TextInput?.Invoke(this, new TextInputEventArgs(c.ToString()));

    private static Point ToPoint(System.Numerics.Vector2 v) => new(v.X, v.Y);
    private static Core.MouseButton Map(SilkMouseButton b) => b switch
    {
        SilkMouseButton.Right => Core.MouseButton.Right,
        SilkMouseButton.Middle => Core.MouseButton.Middle,
        _ => Core.MouseButton.Left,
    };

    public void Present(ReadOnlySpan<byte> pixels, int width, int height) { /* headless path */ }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _input?.Dispose();
        _skiaSurface?.Dispose();
        _renderTarget?.Dispose();
        _grContext?.Dispose();
        _gl?.Dispose();
        _window?.Dispose();
    }
}
