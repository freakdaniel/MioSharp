using System.Text.RegularExpressions;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Css.Parser;
using AngleSharp.Dom;
using Mio.Core;

namespace Mio.Css;

/// <summary>
/// Resolves computed CSS styles for DOM elements.
/// Applies user-agent defaults, stylesheet rules (cascade), and inline styles.
/// </summary>
public sealed class StyleEngine
{
    // Parsed stylesheet rules: (specificity, selector, declarations)
    private readonly List<(int specificity, ISelector selector, ICssStyleDeclaration declarations)> _rules = [];
    private static readonly CssParser _cssParser = new();

    // Viewport dimensions — updated by LayoutEngine before each layout pass
    public static float ViewportWidth  = 1280f;
    public static float ViewportHeight = 720f;

    /// <summary>Parses a CSS string (from a &lt;style&gt; tag or .css file) and registers its rules.</summary>
    public void LoadStylesheet(string css)
    {
        try
        {
            var sheet = _cssParser.ParseStyleSheet(css);
            foreach (var rule in sheet.Rules.OfType<ICssStyleRule>())
            {
                var selector = rule.Selector;
                _rules.Add((ComputeSpecificity(selector.Text), selector, rule.Style));
            }
            // Sort by specificity ascending so higher specificity wins (applied last)
            _rules.Sort((a, b) => a.specificity.CompareTo(b.specificity));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CSS] Parse error: {ex.Message}");
        }
    }

    /// <summary>Clears all loaded stylesheet rules (call before loading a new document).</summary>
    public void ClearStylesheets() => _rules.Clear();

    /// <summary>Computes the final style for an element, walking its ancestor chain.</summary>
    /// <param name="inheritedVars">CSS custom properties inherited from the parent element.</param>
    public ComputedStyle Compute(IElement element, IReadOnlyDictionary<string, string>? inheritedVars = null)
    {
        var style = new ComputedStyle();

        // Seed with inherited CSS variables (child may override)
        if (inheritedVars != null)
            foreach (var kv in inheritedVars)
                style.CssVariables[kv.Key] = kv.Value;

        // 1. User-agent tag defaults
        ApplyTagDefaults(element.TagName.ToLowerInvariant(), style);

        // 2. Stylesheet rules (in specificity order — low to high)
        foreach (var (_, selector, declarations) in _rules)
        {
            try
            {
                if (selector.Match(element))
                    ApplyCssDeclarations(declarations, style);
            }
            catch { /* ignore selector match errors */ }
        }

        // 3. Inline style="" (highest priority)
        ApplyInlineStyle(element, style);

        return style;
    }

    private static void ApplyCssDeclarations(ICssStyleDeclaration decl, ComputedStyle s)
    {
        for (int i = 0; i < decl.Length; i++)
        {
            var prop = decl[i];
            var value = decl.GetPropertyValue(prop);
            if (!string.IsNullOrEmpty(value))
                ApplyProperty(prop, value, s);
        }
    }

    private static int ComputeSpecificity(string selectorText)
    {
        int score = 0;
        score += CountOccurrences(selectorText, '#') * 100;
        score += CountOccurrences(selectorText, '.') * 10;
        score += CountOccurrences(selectorText, ':') * 10;
        score += selectorText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                             .Count(p => !p.StartsWith('.') && !p.StartsWith('#') && !p.StartsWith(':') && !p.StartsWith('[') && p.Length > 0);
        return score;
    }

    private static int CountOccurrences(string s, char c) { int n = 0; foreach (var ch in s) if (ch == c) n++; return n; }

    private static void ApplyTagDefaults(string tag, ComputedStyle s)
    {
        switch (tag)
        {
            case "html":
            case "body":
                s.Display = Display.Block;
                s.Margin = Thickness.Zero;
                s.Padding = Thickness.Zero;
                break;

            case "div":
            case "section":
            case "article":
            case "header":
            case "footer":
            case "main":
            case "nav":
            case "aside":
            case "p":
            case "ul":
            case "ol":
            case "li":
            case "form":
                s.Display = Display.Block;
                break;

            case "span":
            case "a":
            case "strong":
            case "em":
            case "code":
            case "small":
            case "label":
                s.Display = Display.Inline;
                break;

            case "h1":
                s.Display = Display.Block;
                s.FontSize = 32f;
                s.FontWeight = FontWeight.Bold;
                s.Margin = new Thickness(21, 0);
                break;
            case "h2":
                s.Display = Display.Block;
                s.FontSize = 24f;
                s.FontWeight = FontWeight.Bold;
                s.Margin = new Thickness(20, 0);
                break;
            case "h3":
                s.Display = Display.Block;
                s.FontSize = 18.72f;
                s.FontWeight = FontWeight.Bold;
                s.Margin = new Thickness(18, 0);
                break;
            case "h4":
                s.Display = Display.Block;
                s.FontSize = 16f;
                s.FontWeight = FontWeight.Bold;
                s.Margin = new Thickness(21, 0);
                break;
            case "h5":
            case "h6":
                s.Display = Display.Block;
                s.FontSize = 13.28f;
                s.FontWeight = FontWeight.Bold;
                s.Margin = new Thickness(22, 0);
                break;

            case "button":
                s.Display = Display.InlineBlock;
                s.Padding = new Thickness(4, 8);
                s.BorderWidth = new Thickness(1);
                s.BorderColor = new Color(128, 128, 128);
                break;

            case "input":
            case "select":
            case "textarea":
                s.Display = Display.InlineBlock;
                s.Padding = new Thickness(4, 6);
                s.BorderWidth = new Thickness(1);
                s.BorderColor = new Color(128, 128, 128);
                break;

            case "img":
                s.Display = Display.InlineBlock;
                break;

            case "table":
                s.Display = Display.Block;
                break;

            case "script":
            case "style":
            case "head":
            case "meta":
            case "link":
            case "title":
                s.Display = Display.None;
                break;
        }
    }

    private static void ApplyInlineStyle(IElement element, ComputedStyle s)
    {
        var styleAttr = element.GetAttribute("style");
        if (string.IsNullOrWhiteSpace(styleAttr)) return;

        // Simple property: value pair parser (handles most common properties)
        foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0) continue;

            var prop = decl[..colon].Trim().ToLowerInvariant();
            var value = decl[(colon + 1)..].Trim();

            ApplyProperty(prop, value, s);
        }
    }

    public static void ApplyProperty(string prop, string value, ComputedStyle s)
    {
        // CSS custom properties (--variable-name: value)
        if (prop.StartsWith("--"))
        {
            s.CssVariables[prop] = value;
            return;
        }

        // Resolve var(--x) references before applying the property
        if (value.Contains("var(--"))
        {
            var resolved = ResolveVars(value, s.CssVariables);
            if (string.IsNullOrEmpty(resolved)) return;
            value = resolved;
        }

        switch (prop)
        {
            case "display": s.Display = ParseDisplay(value); break;
            case "position": s.Position = ParsePosition(value); break;
            case "overflow": s.Overflow = ParseOverflow(value); break;
            case "box-sizing": s.BoxSizing = value == "border-box" ? BoxSizing.BorderBox : BoxSizing.ContentBox; break;

            case "width": s.Width = ParseLength(value); break;
            case "height": s.Height = ParseLength(value); break;
            case "min-width": s.MinWidth = ParseLength(value); break;
            case "min-height": s.MinHeight = ParseLength(value); break;
            case "max-width": s.MaxWidth = ParseLength(value); break;
            case "max-height": s.MaxHeight = ParseLength(value); break;

            case "top": s.Top = ParseLength(value); break;
            case "right": s.Right = ParseLength(value); break;
            case "bottom": s.Bottom = ParseLength(value); break;
            case "left": s.Left = ParseLength(value); break;

            case "margin": s.Margin = ParseThickness(value); break;
            case "margin-top": s.Margin = s.Margin with { Top = ParseLength(value) ?? 0 }; break;
            case "margin-right": s.Margin = s.Margin with { Right = ParseLength(value) ?? 0 }; break;
            case "margin-bottom": s.Margin = s.Margin with { Bottom = ParseLength(value) ?? 0 }; break;
            case "margin-left": s.Margin = s.Margin with { Left = ParseLength(value) ?? 0 }; break;

            case "padding": s.Padding = ParseThickness(value); break;
            case "padding-top": s.Padding = s.Padding with { Top = ParseLength(value) ?? 0 }; break;
            case "padding-right": s.Padding = s.Padding with { Right = ParseLength(value) ?? 0 }; break;
            case "padding-bottom": s.Padding = s.Padding with { Bottom = ParseLength(value) ?? 0 }; break;
            case "padding-left": s.Padding = s.Padding with { Left = ParseLength(value) ?? 0 }; break;

            case "border-width": s.BorderWidth = ParseThickness(value); break;
            case "border-top-width": s.BorderWidth = s.BorderWidth with { Top = ParseLength(value) ?? 0 }; break;
            case "border-right-width": s.BorderWidth = s.BorderWidth with { Right = ParseLength(value) ?? 0 }; break;
            case "border-bottom-width": s.BorderWidth = s.BorderWidth with { Bottom = ParseLength(value) ?? 0 }; break;
            case "border-left-width": s.BorderWidth = s.BorderWidth with { Left = ParseLength(value) ?? 0 }; break;

            case "border-color": s.BorderColor = ParseColor(value) ?? Color.Black; break;
            case "border-top-color":
            case "border-right-color":
            case "border-bottom-color":
            case "border-left-color":
                { var c = ParseColor(value); if (c.HasValue) s.BorderColor = c.Value; }
                break;

            // border-style is ignored (we only care about width/color)
            case "border-style":
            case "border-top-style":
            case "border-right-style":
            case "border-bottom-style":
            case "border-left-style":
                break;

            case "border-radius": s.BorderRadius = ParseLength(value) ?? 0; break;
            // Individual corner radii — map all to our uniform BorderRadius
            case "border-top-left-radius":
            case "border-top-right-radius":
            case "border-bottom-right-radius":
            case "border-bottom-left-radius":
                { var r = ParseLength(value); if (r.HasValue && s.BorderRadius == 0) s.BorderRadius = r.Value; }
                break;

            case "border":
                ParseBorderShorthand(value, s);
                break;

            case "background":
            case "background-color": s.BackgroundColor = ParseColor(value) ?? Color.Transparent; break;

            case "color": s.Color = ParseColor(value) ?? Color.Black; break;
            case "font-size": s.FontSize = ParseLength(value) ?? 16; break;
            case "font-family": s.FontFamily = value.Trim('"', '\''); break;
            case "font-weight": s.FontWeight = value is "bold" or "700" ? FontWeight.Bold : FontWeight.Normal; break;
            case "font-style": s.FontStyle = value == "italic" ? FontStyle.Italic : FontStyle.Normal; break;
            case "line-height": s.LineHeight = ParseLength(value) ?? 1.2f; break;
            case "text-align": s.TextAlign = ParseTextAlign(value); break;
            case "text-decoration":
            case "text-decoration-line":
                s.TextDecoration = value.Contains("underline") ? TextDecoration.Underline : TextDecoration.None; break;

            case "flex-direction": s.FlexDirection = ParseFlexDirection(value); break;
            case "flex-wrap": s.FlexWrap = value == "wrap" ? FlexWrap.Wrap : FlexWrap.NoWrap; break;
            case "justify-content": s.JustifyContent = ParseJustifyContent(value); break;
            case "align-items": s.AlignItems = ParseAlignItems(value); break;
            case "flex-grow": s.FlexGrow = ParseFloat(value) ?? 0; break;
            case "flex-shrink": s.FlexShrink = ParseFloat(value) ?? 1; break;
            case "flex-basis": s.FlexBasis = ParseLength(value); break;
            case "gap":
            case "column-gap":
            case "row-gap":
                { var gv = ParseLength(value); if (gv.HasValue) s.Gap = gv; }
                break;

            case "z-index": s.ZIndex = (int)(ParseFloat(value) ?? 0); break;
            case "opacity": s.Opacity = ParseFloat(value) ?? 1; break;
            case "cursor": s.Cursor = value; break;

            // CSS Transitions
            case "transition": s.Transitions = ParseTransitions(value); break;
            case "transition-property":
            case "transition-duration":
            case "transition-timing-function":
            case "transition-delay":
                break; // individual sub-properties handled via shorthand only for now

            // Ignore layout-only hints we don't use
            case "visibility":
            case "pointer-events":
            case "user-select":
            case "list-style":
            case "list-style-type":
            case "outline":
            case "outline-color":
            case "outline-width":
            case "outline-style":
            case "resize":
            case "appearance":
            case "white-space":
            case "word-break":
            case "word-wrap":
            case "overflow-x":
            case "overflow-y":
            case "vertical-align":
            case "float":
            case "clear":
            case "table-layout":
            case "border-collapse":
            case "border-spacing":
            case "font":
            case "text-transform":
            case "text-overflow":
            case "text-indent":
            case "letter-spacing":
            case "word-spacing":
            case "content":
            case "animation":
            case "animation-name":
            case "animation-duration":
            case "animation-delay":
            case "animation-iteration-count":
            case "animation-timing-function":
            case "animation-fill-mode":
            case "transform":
            case "transform-origin":
            case "will-change":
            case "filter":
            case "backdrop-filter":
            case "mix-blend-mode":
            case "isolation":
            case "object-fit":
            case "object-position":
            case "aspect-ratio":
            case "grid-template":
            case "grid-template-columns":
            case "grid-template-rows":
            case "grid-template-areas":
            case "grid-area":
            case "grid-column":
            case "grid-row":
            case "place-items":
            case "place-content":
            case "place-self":
            case "align-self":
            case "align-content":
            case "justify-self":
            case "justify-items":
            case "flex-flow":
            case "flex":
            case "background-image":
            case "background-repeat":
            case "background-position":
            case "background-size":
            case "background-attachment":
            case "background-clip":
            case "background-origin":
            case "box-shadow":
            case "text-shadow":
            case "scroll-behavior":
            case "overscroll-behavior":
            case "touch-action":
                break; // intentionally ignored
        }
    }

    private static readonly System.Globalization.CultureInfo InvCulture = System.Globalization.CultureInfo.InvariantCulture;
    private static readonly System.Globalization.NumberStyles FloatStyle = System.Globalization.NumberStyles.Float;

    public static float? ParseLength(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v == "0") return 0f;
        if (v.StartsWith("calc(")) return null; // calc() stub — not supported, return null (use default)
        if (v.EndsWith("px") && float.TryParse(v[..^2], FloatStyle, InvCulture, out var px)) return px;
        if (v.EndsWith("em") && float.TryParse(v[..^2], FloatStyle, InvCulture, out var em)) return em * 16;
        if (v.EndsWith("rem") && float.TryParse(v[..^3], FloatStyle, InvCulture, out var rem)) return rem * 16;
        if (v.EndsWith("vw") && float.TryParse(v[..^2], FloatStyle, InvCulture, out var vw)) return vw * ViewportWidth / 100f;
        if (v.EndsWith("vh") && float.TryParse(v[..^2], FloatStyle, InvCulture, out var vh)) return vh * ViewportHeight / 100f;
        if (v.EndsWith("%") && float.TryParse(v[..^1], FloatStyle, InvCulture, out var pct)) return pct; // parent-relative, resolved later
        if (float.TryParse(v, FloatStyle, InvCulture, out var raw)) return raw;
        return null;
    }

    private static float? ParseFloat(string v)
    {
        v = v.Trim();
        return float.TryParse(v, FloatStyle, InvCulture, out var f) ? f : null;
    }

    /// <summary>Resolves var(--x, fallback) references in a CSS value using the element's CssVariables map.</summary>
    private static string ResolveVars(string value, Dictionary<string, string> vars)
    {
        return Regex.Replace(value, @"var\((--[^,)]+)(?:,\s*([^)]+))?\)", m =>
        {
            var name     = m.Groups[1].Value.Trim();
            var fallback = m.Groups[2].Success ? m.Groups[2].Value.Trim() : "";
            return vars.TryGetValue(name, out var v) ? v : fallback;
        });
    }

    /// <summary>Parses a CSS transition shorthand into a list of TransitionSpec records.</summary>
    private static List<TransitionSpec> ParseTransitions(string value)
    {
        var result = new List<TransitionSpec>();
        foreach (var part in value.Split(','))
        {
            var tokens = part.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) continue;
            var prop     = tokens[0];
            var duration = ParseLengthSeconds(tokens[1]) ?? 0.3f;
            var easing   = tokens.Length > 2 ? tokens[2] : "ease";
            var delay    = tokens.Length > 3 ? (ParseLengthSeconds(tokens[3]) ?? 0f) : 0f;
            result.Add(new TransitionSpec(prop, duration, easing, delay));
        }
        return result;
    }

    /// <summary>Parses a CSS time value (e.g. "0.3s", "300ms") to seconds.</summary>
    private static float? ParseLengthSeconds(string v)
    {
        v = v.Trim().ToLowerInvariant();
        if (v.EndsWith("ms") && float.TryParse(v[..^2], FloatStyle, InvCulture, out var ms)) return ms / 1000f;
        if (v.EndsWith("s")  && float.TryParse(v[..^1], FloatStyle, InvCulture, out var s))  return s;
        return null;
    }

    private static Thickness ParseThickness(string v)
    {
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => new Thickness(ParseLength(parts[0]) ?? 0),
            2 => new Thickness(ParseLength(parts[0]) ?? 0, ParseLength(parts[1]) ?? 0),
            3 => new Thickness(ParseLength(parts[0]) ?? 0, ParseLength(parts[1]) ?? 0, ParseLength(parts[2]) ?? 0, ParseLength(parts[1]) ?? 0),
            4 => new Thickness(ParseLength(parts[0]) ?? 0, ParseLength(parts[1]) ?? 0, ParseLength(parts[2]) ?? 0, ParseLength(parts[3]) ?? 0),
            _ => Thickness.Zero,
        };
    }

    private static void ParseBorderShorthand(string v, ComputedStyle s)
    {
        // e.g. "1px solid #ccc"
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var w = ParseLength(p);
            if (w.HasValue) { s.BorderWidth = new Thickness(w.Value); continue; }
            var c = ParseColor(p);
            if (c.HasValue) { s.BorderColor = c.Value; }
        }
    }

    public static Color? ParseColor(string v)
    {
        v = v.Trim().ToLowerInvariant();

        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
                return new Color((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
            if (hex.Length == 8 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgba))
                return new Color((byte)((rgba >> 24) & 0xFF), (byte)((rgba >> 16) & 0xFF), (byte)((rgba >> 8) & 0xFF), (byte)(rgba & 0xFF));
        }

        if (v.StartsWith("rgb(") || v.StartsWith("rgba("))
        {
            var inner = v[(v.IndexOf('(') + 1)..v.LastIndexOf(')')];
            var parts = inner.Split(',');
            if (parts.Length >= 3)
            {
                byte r = byte.TryParse(parts[0].Trim(), out var rv) ? rv : (byte)0;
                byte g = byte.TryParse(parts[1].Trim(), out var gv) ? gv : (byte)0;
                byte b = byte.TryParse(parts[2].Trim(), out var bv) ? bv : (byte)0;
                byte a = parts.Length >= 4 && float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var av) ? (byte)(av * 255) : (byte)255;
                return new Color(r, g, b, a);
            }
        }

        return v switch
        {
            "transparent" => Color.Transparent,
            "black" => new Color(0, 0, 0),
            "white" => new Color(255, 255, 255),
            "red" => new Color(255, 0, 0),
            "green" => new Color(0, 128, 0),
            "blue" => new Color(0, 0, 255),
            "yellow" => new Color(255, 255, 0),
            "orange" => new Color(255, 165, 0),
            "purple" => new Color(128, 0, 128),
            "pink" => new Color(255, 192, 203),
            "gray" or "grey" => new Color(128, 128, 128),
            "lightgray" or "lightgrey" => new Color(211, 211, 211),
            "darkgray" or "darkgrey" => new Color(169, 169, 169),
            "navy" => new Color(0, 0, 128),
            "teal" => new Color(0, 128, 128),
            "silver" => new Color(192, 192, 192),
            "maroon" => new Color(128, 0, 0),
            _ => null,
        };
    }

    private static Display ParseDisplay(string v) => v switch
    {
        "block" => Display.Block,
        "inline" => Display.Inline,
        "inline-block" => Display.InlineBlock,
        "flex" or "inline-flex" => Display.Flex,
        "grid" or "inline-grid" => Display.Grid,
        "none" => Display.None,
        _ => Display.Block,
    };

    private static Position ParsePosition(string v) => v switch
    {
        "relative" => Position.Relative,
        "absolute" => Position.Absolute,
        "fixed" => Position.Fixed,
        "sticky" => Position.Sticky,
        _ => Position.Static,
    };

    private static Overflow ParseOverflow(string v) => v switch
    {
        "hidden" => Overflow.Hidden,
        "scroll" => Overflow.Scroll,
        "auto" => Overflow.Auto,
        _ => Overflow.Visible,
    };

    private static TextAlign ParseTextAlign(string v) => v switch
    {
        "right" => TextAlign.Right,
        "center" => TextAlign.Center,
        "justify" => TextAlign.Justify,
        _ => TextAlign.Left,
    };

    private static FlexDirection ParseFlexDirection(string v) => v switch
    {
        "row-reverse" => FlexDirection.RowReverse,
        "column" => FlexDirection.Column,
        "column-reverse" => FlexDirection.ColumnReverse,
        _ => FlexDirection.Row,
    };

    private static JustifyContent ParseJustifyContent(string v) => v switch
    {
        "flex-end" or "end" => JustifyContent.FlexEnd,
        "center" => JustifyContent.Center,
        "space-between" => JustifyContent.SpaceBetween,
        "space-around" => JustifyContent.SpaceAround,
        "space-evenly" => JustifyContent.SpaceEvenly,
        _ => JustifyContent.FlexStart,
    };

    private static AlignItems ParseAlignItems(string v) => v switch
    {
        "flex-start" or "start" => AlignItems.FlexStart,
        "flex-end" or "end" => AlignItems.FlexEnd,
        "center" => AlignItems.Center,
        "baseline" => AlignItems.Baseline,
        _ => AlignItems.Stretch,
    };
}
