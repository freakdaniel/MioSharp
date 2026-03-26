namespace Mio.Core;

public enum MouseButton { Left, Middle, Right }

public sealed class MouseEventArgs(Point position, MouseButton button) : EventArgs
{
    public Point Position { get; } = position;
    public MouseButton Button { get; } = button;
}

public sealed class MouseMoveEventArgs(Point position) : EventArgs
{
    public Point Position { get; } = position;
}

public sealed class ScrollEventArgs(Point position, float deltaX, float deltaY) : EventArgs
{
    public Point Position { get; } = position;
    public float DeltaX { get; } = deltaX;
    public float DeltaY { get; } = deltaY;
}

public sealed class KeyEventArgs(string key, string code, bool ctrl, bool alt, bool shift) : EventArgs
{
    public string Key { get; } = key;
    public string Code { get; } = code;
    public bool Ctrl { get; } = ctrl;
    public bool Alt { get; } = alt;
    public bool Shift { get; } = shift;
}

public sealed class TextInputEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;
}

public sealed class ResizeEventArgs(Size size) : EventArgs
{
    public Size Size { get; } = size;
}
