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
    private readonly Func<string, ComputedStyle, float>? _measureText;
    private readonly Func<string, string?>? _loadResource;

    /// <param name="measureText">Optional: returns exact rendered pixel width of a string.
    /// Falls back to charCount × fontSize × 0.55 when null.</param>
    /// <param name="loadResource">Optional: fetches a resource by URL path and returns its text content.
    /// Used to load external stylesheets referenced by &lt;link rel="stylesheet"&gt;.</param>
    public LayoutEngine(StyleEngine style,
        Func<string, ComputedStyle, float>? measureText = null,
        Func<string, string?>? loadResource = null)
    {
        _style = style;
        _measureText = measureText;
        _loadResource = loadResource;
    }

    private float MeasureTextWidth(string text, ComputedStyle style) =>
        _measureText != null ? _measureText(text, style) : text.Length * style.FontSize * 0.55f;

    public LayoutBox LayoutDocument(IDocument document, Size viewport)
    {
        // Load all stylesheets before layout (re-done each frame so JS-mutated styles stay current)
        _style.ClearStylesheets();

        // 1. External <link rel="stylesheet" href="..."> files
        if (_loadResource != null)
        {
            foreach (var link in document.QuerySelectorAll("link"))
            {
                var rel  = link.GetAttribute("rel")  ?? "";
                var href = link.GetAttribute("href") ?? "";
                if (rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(href))
                {
                    var css = _loadResource(href);
                    if (!string.IsNullOrEmpty(css))
                        _style.LoadStylesheet(css);
                }
            }
        }

        // 2. Inline <style> blocks
        foreach (var styleEl in document.QuerySelectorAll("style"))
        {
            if (!string.IsNullOrWhiteSpace(styleEl.TextContent))
                _style.LoadStylesheet(styleEl.TextContent);
        }

        // Sync viewport dimensions for vw/vh unit resolution
        Mio.Css.StyleEngine.ViewportWidth  = viewport.Width;
        Mio.Css.StyleEngine.ViewportHeight = viewport.Height;

        // Seed CSS custom properties from :root (:root = <html> in HTML).
        // Without this, var(--primary) etc. never resolve in body/children.
        var rootVars = new Dictionary<string, string>();
        if (document.DocumentElement != null)
        {
            var htmlStyle = _style.Compute(document.DocumentElement, null);
            foreach (var kv in htmlStyle.CssVariables)
                rootVars[kv.Key] = kv.Value;
        }

        var rootEl = document.Body ?? document.DocumentElement!;
        var root = BuildBox(rootEl, rootVars.Count > 0 ? rootVars : null);
        PerformLayout(root, new Rect(0, 0, viewport.Width, viewport.Height));
        return root;
    }

    private LayoutBox BuildBox(IElement element, IReadOnlyDictionary<string, string>? inheritedVars = null)
    {
        var style = _style.Compute(element, inheritedVars);
        var box = new LayoutBox { Element = element, Style = style };

        if (style.Display == Display.None) return box;

        var tagLower = element.TagName.ToLowerInvariant();

        // Handle <svg> — capture outer HTML for Painter to render via Svg.Skia
        bool isSvgNs = element.GetAttribute("data-ns")?.Contains("svg", StringComparison.OrdinalIgnoreCase) == true;
        if (tagLower == "svg" || isSvgNs)
        {
            var svgHtml = element.OuterHtml;
            if (!style.Width.HasValue && float.TryParse(
                    element.GetAttribute("width"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var svgW))
                style.Width = svgW;
            if (!style.Height.HasValue && float.TryParse(
                    element.GetAttribute("height"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var svgH))
                style.Height = svgH;
            // Default SVG size if neither CSS nor attributes specify it
            style.Width  ??= 300;
            style.Height ??= 150;
            return new LayoutBox { Element = element, Style = style, SvgContent = svgHtml };
        }

        // Handle <img> — capture src for Painter, size from attributes/CSS
        if (tagLower == "img")
        {
            var src = element.GetAttribute("src") ?? "";
            if (!src.StartsWith("http") && !src.StartsWith("/") && !src.StartsWith("data:"))
                src = "/" + src;
            if (!style.Width.HasValue && int.TryParse(element.GetAttribute("width"), out var aw)) style.Width = aw;
            if (!style.Height.HasValue && int.TryParse(element.GetAttribute("height"), out var ah)) style.Height = ah;
            return new LayoutBox { Element = element, Style = style, ImageSrc = src };
        }

        foreach (var child in element.ChildNodes)
        {
            if (child is IText text)
            {
                // Collapse whitespace runs to single space (CSS white-space: normal).
                // Do NOT Trim() — spaces between inline elements are meaningful:
                // "The <strong>only</strong> bridge" needs spaces around "only".
                var txt = System.Text.RegularExpressions.Regex.Replace(text.Data, @"\s+", " ");
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    box.Children.Add(new LayoutBox
                    {
                        TextContent = txt,
                        Style = new ComputedStyle
                        {
                            Display    = Display.Inline,
                            Color      = style.Color,
                            FontSize   = style.FontSize,
                            FontFamily = style.FontFamily,
                            FontWeight = style.FontWeight,
                            FontStyle  = style.FontStyle,
                        }
                    });
                }
            }
            else if (child is IElement childEl)
            {
                // Pass inherited CSS variables down the tree
                var childBox = BuildBox(childEl, style.CssVariables);
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
        // Measures the intrinsic (content-based) width of an inline box by summing its children.
        // Without this, <strong>/<em>/<a> would get the full container width, pushing siblings off-screen.
        float MeasureInlineBoxWidth(LayoutBox inl)
        {
            if (inl.IsTextBox)
                return MeasureTextWidth(inl.TextContent!, inl.Style);
            float total = 0;
            foreach (var child in inl.Children)
                total += MeasureInlineBoxWidth(child);
            return inl.Style.Width ?? (total > 0 ? total : 0);
        }

        void PositionInlineDescendants(LayoutBox inl)
        {
            // Recursively assign ContentRect to children of an already-positioned inline box.
            // Without this, <strong>/<em>/<a> children stay at ContentRect(0,0) and render at top of screen.
            if (inl.IsTextBox || inl.Children.Count == 0) return;
            float cx = inl.ContentRect.X;
            float cy = inl.ContentRect.Y;
            float h  = inl.ContentRect.Height;
            foreach (var child in inl.Children)
            {
                float w = MeasureInlineBoxWidth(child);
                child.ContentRect = new Rect(cx, cy, w, h);
                PositionInlineDescendants(child);
                cx += w;
            }
        }

        void FlushInlineRun()
        {
            if (inlineRun.Count == 0) return;
            float lineH = inlineRun.Max(c => c.Style.FontSize * c.Style.LineHeight);
            float curX = x;
            foreach (var inl in inlineRun)
            {
                float naturalW = MeasureInlineBoxWidth(inl);
                float inlW = innerW < 99_000f ? Math.Min(naturalW, innerW) : naturalW;
                inl.ContentRect = new Rect(curX, cursorY, inlW, lineH);
                PositionInlineDescendants(inl);
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
