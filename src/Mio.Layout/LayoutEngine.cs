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
        float innerW = width; // width is already the content-box width (padding+border already subtracted)

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
                float naturalW = inl.IsTextBox
                    ? inl.TextContent!.Length * inl.Style.FontSize * 0.55f
                    : (inl.Style.Width ?? innerW);
                float inlW = innerW < 99_000f ? Math.Min(naturalW, innerW) : naturalW;
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

        // Shrink-to-fit: when no explicit width and containing block is unconstrained,
        // tighten content width to actual child extent (needed for flex row items)
        if (!box.Style.Width.HasValue && containing.Width >= 99_000f)
        {
            float maxRight = 0;
            foreach (var c in box.Children)
                maxRight = Math.Max(maxRight, c.ContentRect.Right - x);
            if (maxRight > 0)
            {
                innerW = maxRight;
                box.ContentRect = box.ContentRect with { Width = innerW };
            }
        }

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

        const float Unconstrained = 100_000f;
        float totalFixed = 0, totalGrow = 0;
        foreach (var child in children)
        {
            var intrinsic = new Rect(x, y,
                isRow ? (child.Style.Width ?? Unconstrained) : innerW,
                isRow ? innerH : (child.Style.Height ?? Unconstrained));
            PerformLayout(child, intrinsic);

            totalFixed += isRow
                ? child.MarginRect.Width  - child.Style.Margin.Horizontal
                : child.MarginRect.Height - child.Style.Margin.Vertical;
            totalGrow += child.Style.FlexGrow;
        }

        float totalGapSpace = gap * (children.Count - 1);
        float freeSpace = (isRow ? innerW : innerH) - totalFixed - totalGapSpace;
        if (freeSpace < 0) freeSpace = 0;

        float offset = 0, spaceBetween = 0, spaceAround = 0;
        switch (box.Style.JustifyContent)
        {
            case JustifyContent.FlexEnd: offset = freeSpace; break;
            case JustifyContent.Center: offset = freeSpace / 2; break;
            case JustifyContent.SpaceBetween: spaceBetween = children.Count > 1 ? freeSpace / (children.Count - 1) : 0; break;
            case JustifyContent.SpaceAround: spaceAround = freeSpace / children.Count; offset = spaceAround / 2; break;
            case JustifyContent.SpaceEvenly: spaceAround = freeSpace / (children.Count + 1); offset = spaceAround; break;
        }

        float cursor = isRow ? (x + offset) : (y + offset);
        foreach (var child in children)
        {
            float mainBorder = isRow
                ? child.MarginRect.Width  - child.Style.Margin.Horizontal
                : child.MarginRect.Height - child.Style.Margin.Vertical;
            if (child.Style.FlexGrow > 0 && totalGrow > 0)
                mainBorder += freeSpace * (child.Style.FlexGrow / totalGrow);

            float crossBorder = isRow
                ? child.MarginRect.Height - child.Style.Margin.Vertical
                : child.MarginRect.Width  - child.Style.Margin.Horizontal;

            bool crossUnconstrained = isRow ? innerH >= 99_000f : innerW >= 99_000f;
            if (box.Style.AlignItems == AlignItems.Stretch && !crossUnconstrained)
            {
                if (isRow && !child.Style.Height.HasValue)
                    crossBorder = Math.Max(0, innerH - child.Style.Margin.Vertical);
                else if (!isRow && !child.Style.Width.HasValue)
                    crossBorder = Math.Max(0, innerW - child.Style.Margin.Horizontal);
            }

            float crossInner = isRow ? innerH : innerW;
            float crossStart = isRow ? y : x;
            float crossMarginSize = crossBorder + (isRow ? child.Style.Margin.Vertical : child.Style.Margin.Horizontal);
            float crossBorderStart = AlignCross(crossStart, crossInner, crossMarginSize, box.Style.AlignItems)
                                   + (isRow ? child.Style.Margin.Top : child.Style.Margin.Left);

            float mainBorderStart = cursor + (isRow ? child.Style.Margin.Left : child.Style.Margin.Top);

            float pl = child.Style.Padding.Left,  pr = child.Style.Padding.Right;
            float pt = child.Style.Padding.Top,   pb = child.Style.Padding.Bottom;
            float bl = child.Style.BorderWidth.Left, br = child.Style.BorderWidth.Right;
            float bt = child.Style.BorderWidth.Top,  bb = child.Style.BorderWidth.Bottom;

            float contentX, contentY, contentW, contentH;
            if (isRow)
            {
                contentX = mainBorderStart + bl + pl;
                contentY = crossBorderStart + bt + pt;
                contentW = Math.Max(0, mainBorder  - (bl + br + pl + pr));
                contentH = Math.Max(0, crossBorder - (bt + bb + pt + pb));
            }
            else
            {
                contentX = crossBorderStart + bl + pl;
                contentY = mainBorderStart  + bt + pt;
                contentW = Math.Max(0, crossBorder - (bl + br + pl + pr));
                contentH = Math.Max(0, mainBorder  - (bt + bb + pt + pb));
            }

            child.ContentRect = new Rect(contentX, contentY, contentW, contentH);

            PerformLayout(child, new Rect(
                contentX - child.Style.Margin.Left - bl - pl,
                contentY - child.Style.Margin.Top  - bt - pt,
                Math.Max(0, contentW + child.Style.Margin.Horizontal + bl + br + pl + pr),
                Math.Max(0, contentH + child.Style.Margin.Vertical   + bt + bb + pt + pb)));

            float itemMainTotal = (isRow ? child.Style.Margin.Left : child.Style.Margin.Top)
                                + mainBorder
                                + (isRow ? child.Style.Margin.Right : child.Style.Margin.Bottom);
            cursor += itemMainTotal + gap + spaceBetween + spaceAround;
        }

        if (!box.Style.Height.HasValue)
        {
            float maxCross = children.Max(c => isRow ? c.MarginRect.Height : c.MarginRect.Width);
            box.ContentRect = box.ContentRect with { Height = isRow ? maxCross : Math.Max(0, cursor - y) };
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
