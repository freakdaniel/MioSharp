using AngleSharp.Dom;
using Mio.Core;
using Mio.Css;

namespace Mio.Layout;

/// <summary>
/// Builds a layout tree from a DOM document and performs block/inline/flex layout.
/// </summary>
public sealed class LayoutEngine
{
    private readonly StyleEngine _style;

    public LayoutEngine(StyleEngine style) => _style = style;

    public LayoutBox LayoutDocument(IDocument document, Size viewport)
    {
        // Load all <style> tag contents into the StyleEngine before layout
        _style.ClearStylesheets();
        foreach (var styleEl in document.QuerySelectorAll("style"))
        {
            if (!string.IsNullOrWhiteSpace(styleEl.TextContent))
                _style.LoadStylesheet(styleEl.TextContent);
        }

        var root = BuildBox(document.Body ?? document.DocumentElement);
        PerformLayout(root, new Rect(0, 0, viewport.Width, viewport.Height));
        return root;
    }

    private LayoutBox BuildBox(IElement element)
    {
        var style = _style.Compute(element);
        var box = new LayoutBox { Element = element, Style = style };

        if (style.Display == Display.None) return box;

        foreach (var child in element.ChildNodes)
        {
            if (child is IText text)
            {
                var txt = text.Data.Trim();
                if (!string.IsNullOrEmpty(txt))
                {
                    box.Children.Add(new LayoutBox
                    {
                        TextContent = txt,
                        Style = new ComputedStyle
                        {
                            Display = Display.Inline,
                            Color = style.Color,
                            FontSize = style.FontSize,
                            FontFamily = style.FontFamily,
                            FontWeight = style.FontWeight,
                            FontStyle = style.FontStyle,
                        }
                    });
                }
            }
            else if (child is IElement childEl)
            {
                var childBox = BuildBox(childEl);
                if (childBox.Style.Display != Display.None)
                    box.Children.Add(childBox);
            }
        }

        return box;
    }

    private void PerformLayout(LayoutBox box, Rect containingBlock)
    {
        if (box.Style.Display == Display.Flex)
            FlexLayout(box, containingBlock);
        else
            BlockLayout(box, containingBlock);
    }

    private void BlockLayout(LayoutBox box, Rect containing)
    {
        // For text/inline leaf boxes: measure intrinsic size and return
        if (box.IsTextBox)
        {
            float lineH = box.Style.FontSize * box.Style.LineHeight;
            box.ContentRect = new Rect(containing.X, containing.Y, containing.Width, lineH);
            return;
        }

        // Resolve width
        float width = box.Style.Width
            ?? containing.Width
               - box.Style.Margin.Horizontal
               - box.Style.Padding.Horizontal
               - box.Style.BorderWidth.Horizontal;
        width = Math.Max(0, width);
        if (box.Style.MaxWidth.HasValue) width = Math.Min(width, box.Style.MaxWidth.Value);
        if (box.Style.MinWidth.HasValue) width = Math.Max(width, box.Style.MinWidth.Value);

        float x = containing.X + box.Style.Margin.Left + box.Style.BorderWidth.Left + box.Style.Padding.Left;
        float y = containing.Y + box.Style.Margin.Top + box.Style.BorderWidth.Top + box.Style.Padding.Top;
        float innerW = width - box.Style.Padding.Horizontal - box.Style.BorderWidth.Horizontal;

        box.ContentRect = new Rect(x, y, innerW, 0);

        float cursorY = y;

        // Group consecutive inline/text children into line runs
        var inlineRun = new List<LayoutBox>();
        void FlushInlineRun()
        {
            if (inlineRun.Count == 0) return;
            float lineH = inlineRun.Max(c => c.Style.FontSize * c.Style.LineHeight);
            float curX = x;
            foreach (var inl in inlineRun)
            {
                // Approximate inline width by text length * fontsize * 0.55
                float inlW = inl.IsTextBox
                    ? Math.Min(inl.TextContent!.Length * inl.Style.FontSize * 0.55f, innerW)
                    : (inl.Style.Width ?? innerW);
                inl.ContentRect = new Rect(curX, cursorY, inlW, lineH);
                curX += inlW;
            }
            cursorY += lineH;
            inlineRun.Clear();
        }

        foreach (var child in box.Children)
        {
            bool isInline = child.Style.Display == Display.Inline || child.IsTextBox;

            if (isInline)
            {
                inlineRun.Add(child);
            }
            else
            {
                FlushInlineRun();
                var childContaining = new Rect(x, cursorY, innerW,
                    Math.Max(0, containing.Height - (cursorY - containing.Y)));
                PerformLayout(child, childContaining);
                cursorY += child.MarginRect.Height;
            }
        }
        FlushInlineRun();

        float height = box.Style.Height ?? (cursorY - y);
        if (box.Style.MaxHeight.HasValue) height = Math.Min(height, box.Style.MaxHeight.Value);
        if (box.Style.MinHeight.HasValue) height = Math.Max(height, box.Style.MinHeight.Value);
        box.ContentRect = box.ContentRect with { Height = Math.Max(0, height) };
    }

    private void FlexLayout(LayoutBox box, Rect containing)
    {
        bool isRow = box.Style.FlexDirection is FlexDirection.Row or FlexDirection.RowReverse;
        float containerW = box.Style.Width ?? containing.Width - box.Style.Margin.Horizontal;
        float containerH = box.Style.Height ?? containing.Height;
        float gap = box.Style.Gap ?? 0;

        float x = containing.X + box.Style.Margin.Left + box.Style.BorderWidth.Left + box.Style.Padding.Left;
        float y = containing.Y + box.Style.Margin.Top + box.Style.BorderWidth.Top + box.Style.Padding.Top;
        float innerW = containerW - box.Style.Padding.Horizontal - box.Style.BorderWidth.Horizontal;
        float innerH = containerH - box.Style.Padding.Vertical - box.Style.BorderWidth.Vertical;

        box.ContentRect = new Rect(x, y, innerW, innerH);

        var children = box.Children.Where(c => c.Style.Display != Display.None).ToList();
        if (children.Count == 0) return;

        // First pass: measure intrinsic sizes using unconstrained space
        const float Unconstrained = 100_000f;
        float totalFixed = 0, totalGrow = 0;
        foreach (var child in children)
        {
            // For column: give full width but unconstrained height so BlockLayout can measure true content height
            // For row: give unconstrained width but full height
            var intrinsic = new Rect(x, y,
                isRow ? (child.Style.Width ?? Unconstrained) : innerW,
                isRow ? innerH : (child.Style.Height ?? Unconstrained));
            PerformLayout(child, intrinsic);
            totalFixed += isRow
                ? child.ContentRect.Width  + child.Style.Margin.Horizontal
                : child.ContentRect.Height + child.Style.Margin.Vertical;
            totalGrow += child.Style.FlexGrow;
        }

        float totalGapSpace = gap * (children.Count - 1);
        float freeSpace = (isRow ? innerW : innerH) - totalFixed - totalGapSpace;
        if (freeSpace < 0) freeSpace = 0;

        // JustifyContent offset
        float offset = 0, spaceBetween = 0, spaceAround = 0;
        switch (box.Style.JustifyContent)
        {
            case JustifyContent.FlexEnd: offset = freeSpace; break;
            case JustifyContent.Center: offset = freeSpace / 2; break;
            case JustifyContent.SpaceBetween: spaceBetween = children.Count > 1 ? freeSpace / (children.Count - 1) : 0; break;
            case JustifyContent.SpaceAround: spaceAround = freeSpace / children.Count; offset = spaceAround / 2; break;
            case JustifyContent.SpaceEvenly: spaceAround = freeSpace / (children.Count + 1); offset = spaceAround; break;
        }

        // Second pass: position children
        float cursor = isRow ? (x + offset) : (y + offset);
        foreach (var child in children)
        {
            float mainSize = isRow ? child.ContentRect.Width : child.ContentRect.Height;
            if (child.Style.FlexGrow > 0 && totalGrow > 0)
                mainSize += freeSpace * (child.Style.FlexGrow / totalGrow);

            float crossPos = isRow
                ? AlignCross(y, innerH, child.ContentRect.Height + child.Style.Margin.Vertical, box.Style.AlignItems)
                : AlignCross(x, innerW, child.ContentRect.Width + child.Style.Margin.Horizontal, box.Style.AlignItems);

            var childRect = isRow
                ? new Rect(cursor + child.Style.Margin.Left, crossPos + child.Style.Margin.Top, mainSize, child.ContentRect.Height)
                : new Rect(crossPos + child.Style.Margin.Left, cursor + child.Style.Margin.Top, child.ContentRect.Width, mainSize);

            child.ContentRect = childRect;

            // Re-layout child with final size so its children get correct positions
            PerformLayout(child, new Rect(childRect.X - child.Style.Padding.Left, childRect.Y - child.Style.Padding.Top,
                childRect.Width + child.Style.Padding.Horizontal, childRect.Height + child.Style.Padding.Vertical));

            cursor += (isRow ? mainSize + child.Style.Margin.Horizontal : mainSize + child.Style.Margin.Vertical)
                     + gap + spaceBetween + spaceAround;
        }

        // Resolve container height if auto
        if (!box.Style.Height.HasValue)
        {
            float maxCross = children.Max(c => isRow ? c.MarginRect.Height : c.MarginRect.Width);
            box.ContentRect = box.ContentRect with { Height = isRow ? maxCross : (cursor - y) };
        }
    }

    private static float AlignCross(float start, float containerSize, float childSize, AlignItems align) =>
        align switch
        {
            AlignItems.FlexEnd => start + containerSize - childSize,
            AlignItems.Center => start + (containerSize - childSize) / 2,
            _ => start,
        };
}
