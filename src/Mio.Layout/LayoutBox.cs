using AngleSharp.Dom;
using Mio.Core;
using Mio.Css;

namespace Mio.Layout;

/// <summary>A positioned, sized box corresponding to one DOM element.</summary>
public sealed class LayoutBox
{
    public IElement? Element { get; init; }
    public string? TextContent { get; init; }  // for text nodes rendered as boxes
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
}
