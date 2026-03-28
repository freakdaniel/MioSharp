using AngleSharp;
using AngleSharp.Dom;
using Mio.Css;
using Mio.Core;

namespace Mio.Layout.Tests;

/// <summary>
/// Tests for StyleEngine CSS parsing, custom properties, var() resolution,
/// border-radius shorthand, and selector matching.
/// </summary>
public class StyleEngineTests
{
    private readonly StyleEngine _style = new();

    private IDocument CreateDoc(string html)
    {
        var context = BrowsingContext.New(Configuration.Default);
        return context.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
    }

    [Fact]
    public void CustomProperties_ParsedFromRoot()
    {
        _style.LoadStylesheet(":root { --primary: #42b883; --radius: 8px; }");
        var doc = CreateDoc("<html><body></body></html>");
        var htmlStyle = _style.Compute(doc.DocumentElement!);
        Assert.Equal("#42b883", htmlStyle.CssVariables["--primary"]);
        Assert.Equal("8px", htmlStyle.CssVariables["--radius"]);
    }

    [Fact]
    public void VarResolution_InBorderRadius()
    {
        _style.LoadStylesheet(":root { --radius: 8px; } .card { border-radius: var(--radius); }");
        var doc = CreateDoc("<html><body><div class='card'></div></body></html>");

        // Compute root vars first (like LayoutEngine does)
        var rootVars = new Dictionary<string, string>();
        var htmlStyle = _style.Compute(doc.DocumentElement!, null);
        foreach (var kv in htmlStyle.CssVariables)
            rootVars[kv.Key] = kv.Value;

        var card = doc.QuerySelector(".card")!;
        var style = _style.Compute(card, rootVars);
        Assert.True(style.BorderRadius.HasRadius);
        Assert.Equal(8f, style.BorderRadius.TopLeft);
        Assert.Equal(8f, style.BorderRadius.TopRight);
        Assert.Equal(8f, style.BorderRadius.BottomRight);
        Assert.Equal(8f, style.BorderRadius.BottomLeft);
    }

    [Fact]
    public void VarResolution_InColor()
    {
        _style.LoadStylesheet(":root { --text: #2c3e50; } p { color: var(--text); }");
        var doc = CreateDoc("<html><body><p>test</p></body></html>");

        var rootVars = new Dictionary<string, string>();
        var htmlStyle = _style.Compute(doc.DocumentElement!, null);
        foreach (var kv in htmlStyle.CssVariables) rootVars[kv.Key] = kv.Value;

        var p = doc.QuerySelector("p")!;
        var style = _style.Compute(p, rootVars);
        Assert.Equal(0x2c, style.Color.R);
        Assert.Equal(0x3e, style.Color.G);
        Assert.Equal(0x50, style.Color.B);
    }

    [Fact]
    public void VarResolution_WithFallback()
    {
        _style.LoadStylesheet(".test { color: var(--missing, red); }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(255, style.Color.R);
        Assert.Equal(0, style.Color.G);
    }

    [Fact]
    public void VarResolution_InBackgroundColor()
    {
        _style.LoadStylesheet(":root { --surface: #ffffff; } .card { background-color: var(--surface); }");
        var doc = CreateDoc("<html><body><div class='card'></div></body></html>");

        var rootVars = new Dictionary<string, string>();
        var htmlStyle = _style.Compute(doc.DocumentElement!);
        foreach (var kv in htmlStyle.CssVariables) rootVars[kv.Key] = kv.Value;

        var style = _style.Compute(doc.QuerySelector(".card")!, rootVars);
        Assert.Equal(255, style.BackgroundColor.R);
        Assert.Equal(255, style.BackgroundColor.G);
        Assert.Equal(255, style.BackgroundColor.B);
        Assert.True(style.BackgroundColor.A > 0);
    }

    [Fact]
    public void BorderRadius_SingleValue()
    {
        _style.LoadStylesheet(".test { border-radius: 4px; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.True(style.BorderRadius.HasRadius);
        Assert.True(style.BorderRadius.IsUniform);
        Assert.Equal(4f, style.BorderRadius.Uniform);
    }

    [Fact]
    public void BorderRadius_TwoValues()
    {
        var result = InvokeParseBorderRadius("8px 4px");
        Assert.Equal(8f, result.TopLeft);
        Assert.Equal(4f, result.TopRight);
        Assert.Equal(8f, result.BottomRight);
        Assert.Equal(4f, result.BottomLeft);
    }

    [Fact]
    public void BorderRadius_FourValues()
    {
        var result = InvokeParseBorderRadius("1px 2px 3px 4px");
        Assert.Equal(1f, result.TopLeft);
        Assert.Equal(2f, result.TopRight);
        Assert.Equal(3f, result.BottomRight);
        Assert.Equal(4f, result.BottomLeft);
    }

    [Fact]
    public void ParseLength_Px()
    {
        Assert.Equal(16f, StyleEngine.ParseLength("16px"));
    }

    [Fact]
    public void ParseLength_Em()
    {
        Assert.Equal(32f, StyleEngine.ParseLength("2em")); // 2 * 16
    }

    [Fact]
    public void ParseLength_Rem()
    {
        Assert.Equal(32f, StyleEngine.ParseLength("2rem")); // 2 * 16
    }

    [Fact]
    public void ParseLength_Zero()
    {
        Assert.Equal(0f, StyleEngine.ParseLength("0"));
    }

    [Fact]
    public void ParseLength_MultiValue_TakesFirst()
    {
        // AngleSharp may expand border-radius corners to "4px 4px"
        Assert.Equal(4f, StyleEngine.ParseLength("4px 4px"));
    }

    [Fact]
    public void ParseLength_Calc_ReturnsNull()
    {
        Assert.Null(StyleEngine.ParseLength("calc(100% - 20px)"));
    }

    [Fact]
    public void ParseColor_Hex3()
    {
        var c = StyleEngine.ParseColor("#f00");
        Assert.NotNull(c);
        Assert.Equal(255, c!.Value.R);
        Assert.Equal(0, c!.Value.G);
    }

    [Fact]
    public void ParseColor_Hex6()
    {
        var c = StyleEngine.ParseColor("#42b883");
        Assert.NotNull(c);
        Assert.Equal(0x42, c!.Value.R);
        Assert.Equal(0xb8, c!.Value.G);
        Assert.Equal(0x83, c!.Value.B);
    }

    [Fact]
    public void ParseColor_Rgba()
    {
        var c = StyleEngine.ParseColor("rgba(255, 0, 0, 0.5)");
        Assert.NotNull(c);
        Assert.Equal(255, c!.Value.R);
        Assert.Equal(0, c!.Value.G);
        Assert.Equal(127, c!.Value.A); // 0.5 * 255 ≈ 127
    }

    [Fact]
    public void ParseColor_Named()
    {
        var c = StyleEngine.ParseColor("red");
        Assert.NotNull(c);
        Assert.Equal(255, c!.Value.R);
    }

    [Fact]
    public void Display_Flex()
    {
        _style.LoadStylesheet(".flex { display: flex; }");
        var doc = CreateDoc("<html><body><div class='flex'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".flex")!);
        Assert.Equal(Display.Flex, style.Display);
    }

    [Fact]
    public void Display_None()
    {
        _style.LoadStylesheet(".hidden { display: none; }");
        var doc = CreateDoc("<html><body><div class='hidden'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".hidden")!);
        Assert.Equal(Display.None, style.Display);
    }

    [Fact]
    public void Padding_Shorthand_FourValues()
    {
        _style.LoadStylesheet(".test { padding: 10px 20px 30px 40px; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(10f, style.Padding.Top);
        Assert.Equal(20f, style.Padding.Right);
        Assert.Equal(30f, style.Padding.Bottom);
        Assert.Equal(40f, style.Padding.Left);
    }

    [Fact]
    public void Margin_Shorthand_TwoValues()
    {
        _style.LoadStylesheet(".test { margin: 10px 20px; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(10f, style.Margin.Top);
        Assert.Equal(20f, style.Margin.Right);
        Assert.Equal(10f, style.Margin.Bottom);
        Assert.Equal(20f, style.Margin.Left);
    }

    [Fact]
    public void Border_Shorthand()
    {
        _style.LoadStylesheet(".test { border: 1px solid #ccc; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(1f, style.BorderWidth.Top);
    }

    [Fact]
    public void FlexDirection_Column()
    {
        _style.LoadStylesheet(".test { display: flex; flex-direction: column; gap: 16px; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(Display.Flex, style.Display);
        Assert.Equal(FlexDirection.Column, style.FlexDirection);
        Assert.Equal(16f, style.Gap);
    }

    [Fact]
    public void ScriptTag_DisplayNone()
    {
        var doc = CreateDoc("<html><body><script></script></body></html>");
        var style = _style.Compute(doc.QuerySelector("script")!);
        Assert.Equal(Display.None, style.Display);
    }

    [Fact]
    public void StyleTag_DisplayNone()
    {
        var doc = CreateDoc("<html><body><style></style></body></html>");
        var style = _style.Compute(doc.QuerySelector("style")!);
        Assert.Equal(Display.None, style.Display);
    }

    [Fact]
    public void H1_HasBoldAndSize32()
    {
        var doc = CreateDoc("<html><body><h1>Title</h1></body></html>");
        var style = _style.Compute(doc.QuerySelector("h1")!);
        Assert.Equal(FontWeight.Bold, style.FontWeight);
        Assert.Equal(32f, style.FontSize);
    }

    [Fact]
    public void ClearStylesheets_RemovesAllRules()
    {
        _style.LoadStylesheet(".test { color: red; }");
        _style.ClearStylesheets();
        _style.LoadStylesheet(".test { color: blue; }");
        var doc = CreateDoc("<html><body><div class='test'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(0, style.Color.R);
        Assert.Equal(0, style.Color.G);
        Assert.Equal(255, style.Color.B);
    }

    [Fact]
    public void InlineStyle_OverridesStylesheet()
    {
        _style.LoadStylesheet(".test { color: blue; }");
        var doc = CreateDoc("<html><body><div class='test' style='color: red'></div></body></html>");
        var style = _style.Compute(doc.QuerySelector(".test")!);
        Assert.Equal(255, style.Color.R);
        Assert.Equal(0, style.Color.B);
    }

    [Fact]
    public void VueLikeCss_CustomPropsAndVarUsage()
    {
        // Simulates what Vue 3 IIFE injects via <style> element
        var css = @":root{--primary: #42b883;--surface: #ffffff;--bg: #f5f5f5;--border: #e0e0e0;--text: #2c3e50}
            *{box-sizing:border-box}
            body{font-family:sans-serif;background:var(--bg);margin:0;padding:20px;color:var(--text)}
            .card{background:var(--surface);border:1px solid var(--border);border-radius:8px;padding:20px}
            h1{color:var(--primary);margin:0 0 8px}";

        _style.LoadStylesheet(css);
        var doc = CreateDoc("<html><body><div class='card'><h1>Title</h1></div></body></html>");

        var rootVars = new Dictionary<string, string>();
        var htmlStyle = _style.Compute(doc.DocumentElement!);
        foreach (var kv in htmlStyle.CssVariables) rootVars[kv.Key] = kv.Value;

        // Card should have white background and 8px border-radius
        var cardStyle = _style.Compute(doc.QuerySelector(".card")!, rootVars);
        Assert.Equal(255, cardStyle.BackgroundColor.R);
        Assert.Equal(255, cardStyle.BackgroundColor.G);
        Assert.Equal(255, cardStyle.BackgroundColor.B);
        Assert.True(cardStyle.BorderRadius.HasRadius);
        Assert.Equal(8f, cardStyle.BorderRadius.Uniform);

        // H1 should have --primary color
        var h1Style = _style.Compute(doc.QuerySelector("h1")!, rootVars);
        Assert.Equal(0x42, h1Style.Color.R);
        Assert.Equal(0xb8, h1Style.Color.G);
        Assert.Equal(0x83, h1Style.Color.B);
    }

    private static CornerRadii InvokeParseBorderRadius(string value)
    {
        var style = new ComputedStyle();
        StyleEngine.ApplyProperty("border-radius", value, style);
        return style.BorderRadius;
    }
}
