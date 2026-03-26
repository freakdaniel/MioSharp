namespace Mio.Core;

/// <summary>
/// Abstraction over the native window surface that the renderer paints into.
/// </summary>
public interface IWindowSurface
{
    Size Size { get; }
    float DevicePixelRatio { get; }

    /// <summary>Paints the given BGRA pixel data into the window.</summary>
    void Present(ReadOnlySpan<byte> pixels, int width, int height);

    event EventHandler<ResizeEventArgs> Resized;
    event EventHandler<MouseEventArgs> MouseDown;
    event EventHandler<MouseEventArgs> MouseUp;
    event EventHandler<MouseMoveEventArgs> MouseMove;
    event EventHandler<ScrollEventArgs> Scroll;
    event EventHandler<KeyEventArgs> KeyDown;
    event EventHandler<KeyEventArgs> KeyUp;
    event EventHandler<TextInputEventArgs> TextInput;
    event EventHandler Closed;
}
