using AngleSharp.Dom;
using Mio.Core;
using Mio.Css;

namespace Mio.Layout;

/// <summary>A positioned, sized box corresponding to one DOM element.</summary>
public sealed class LayoutBox
{
    public IElement? Element { get; init; }
    public string? TextContent { get; init; }  // for text nodes rendered as boxes
    public string? ImageSrc { get; init; }     // for <img> elements — URL path for Painter
    public string? SvgContent { get; init; }   // for <svg> elements — outer HTML rendered via Svg.Skia
    public ComputedStyle Style { get; init; } = new();

    /// <summary>The content rectangle (does not include margin).</summary>
    public Rect ContentRect { get; set; }

    /// <summary>The border-box rectangle (content + padding + border).</summary>
    public Rect BorderRect => new(
        ContentRect.X - Style.Padding.Left - Style.BorderWidth.Left,
        ContentRect.Y - Style.Padding.Top - Style.BorderWidth.Top,
        ContentRect.Width + Style.Padding.Horizontal + Style.BorderWidth.Horizontal,
        ContentRect.Height + Style.Padding.Vertical + Style.BorderWidth.Vertical);

    /// <summary>The margin-box rectangle (border-box + margin).</summary>
    public Rect MarginRect => new(
        BorderRect.X - Style.Margin.Left,
        BorderRect.Y - Style.Margin.Top,
        BorderRect.Width + Style.Margin.Horizontal,
        BorderRect.Height + Style.Margin.Vertical);

    public List<LayoutBox> Children { get; } = [];

    public bool IsTextBox => TextContent != null;

    /// <summary>
    /// Returns the deepest LayoutBox whose BorderRect contains <paramref name="p"/>,
    /// or null if the point misses this box entirely.
    /// Text nodes (IsTextBox) are skipped — their parent element is returned instead.
    /// </summary>
    public LayoutBox? HitTest(Core.Point p)
    {
        if (!BorderRect.Contains(p)) return null;
        // Depth-first: last child wins (painted last = on top)
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var hit = Children[i].HitTest(p);
            if (hit != null) return hit;
        }
        return Element != null ? this : null;
    }
}
