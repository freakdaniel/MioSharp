namespace Mio.Core;

public record struct Size(float Width, float Height)
{
    public static readonly Size Empty = new(0, 0);
}

public record struct Point(float X, float Y)
{
    public static readonly Point Zero = new(0, 0);
}

public record struct Rect(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public static readonly Rect Empty = new(0, 0, 0, 0);

    public bool Contains(Point p) =>
        p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
}

public record struct Thickness(float Top, float Right, float Bottom, float Left)
{
    public static readonly Thickness Zero = new(0, 0, 0, 0);

    public Thickness(float all) : this(all, all, all, all) { }
    public Thickness(float vertical, float horizontal) : this(vertical, horizontal, vertical, horizontal) { }

    public float Horizontal => Left + Right;
    public float Vertical => Top + Bottom;
}

public record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color White = new(255, 255, 255);

    public uint ToArgb() => (uint)((A << 24) | (R << 16) | (G << 8) | B);
}
