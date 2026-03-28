using Mio.Core;

namespace Mio.Css;

public enum Display { Block, Inline, InlineBlock, Flex, Grid, None }
public enum Position { Static, Relative, Absolute, Fixed, Sticky }
public enum FlexDirection { Row, RowReverse, Column, ColumnReverse }
public enum JustifyContent { FlexStart, FlexEnd, Center, SpaceBetween, SpaceAround, SpaceEvenly }
public enum AlignItems { Stretch, FlexStart, FlexEnd, Center, Baseline }
public enum FlexWrap { NoWrap, Wrap, WrapReverse }
public enum Overflow { Visible, Hidden, Scroll, Auto }
public enum TextAlign { Left, Right, Center, Justify }
public enum FontStyle { Normal, Italic, Oblique }
public enum FontWeight { Normal = 400, Bold = 700 }
public enum TextDecoration { None, Underline, Overline, LineThrough }
public enum BoxSizing { ContentBox, BorderBox }

/// <summary>A single CSS transition specification parsed from the transition shorthand.</summary>
public record TransitionSpec(string Property, float Duration, string Easing, float Delay);

/// <summary>Per-corner border radii matching CSS border-radius (TL, TR, BR, BL).</summary>
public record struct CornerRadii(float TopLeft, float TopRight, float BottomRight, float BottomLeft)
{
    public static readonly CornerRadii Zero = new(0, 0, 0, 0);
    public CornerRadii(float all) : this(all, all, all, all) { }
    public bool HasRadius => TopLeft > 0 || TopRight > 0 || BottomRight > 0 || BottomLeft > 0;
    /// <summary>True if all four corners share the same radius.</summary>
    public bool IsUniform => TopLeft == TopRight && TopRight == BottomRight && BottomRight == BottomLeft;
    public float Uniform => TopLeft; // only valid when IsUniform
}

/// <summary>Fully resolved CSS properties for a single element.</summary>
public sealed class ComputedStyle
{
    // Display
    public Display Display { get; set; } = Display.Block;
    public Position Position { get; set; } = Position.Static;
    public Overflow Overflow { get; set; } = Overflow.Visible;
    public BoxSizing BoxSizing { get; set; } = BoxSizing.ContentBox;

    // Dimensions
    public float? Width { get; set; }
    public float? Height { get; set; }
    public float? MinWidth { get; set; }
    public float? MinHeight { get; set; }
    public float? MaxWidth { get; set; }
    public float? MaxHeight { get; set; }

    // Offsets (for positioned elements)
    public float? Top { get; set; }
    public float? Right { get; set; }
    public float? Bottom { get; set; }
    public float? Left { get; set; }

    // Box model
    public Thickness Margin { get; set; } = Thickness.Zero;
    public Thickness Padding { get; set; } = Thickness.Zero;
    public Thickness BorderWidth { get; set; } = Thickness.Zero;
    public Color BorderColor { get; set; } = Color.Transparent;
    public CornerRadii BorderRadius { get; set; } = CornerRadii.Zero;

    // Background
    public Color BackgroundColor { get; set; } = Color.Transparent;

    // Text
    public Color Color { get; set; } = Color.Black;
    public float FontSize { get; set; } = 16f;
    public string FontFamily { get; set; } = "sans-serif";
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public float LineHeight { get; set; } = 1.2f;
    public TextAlign TextAlign { get; set; } = TextAlign.Left;
    public TextDecoration TextDecoration { get; set; } = TextDecoration.None;

    // Flexbox (when Display == Flex)
    public FlexDirection FlexDirection { get; set; } = FlexDirection.Row;
    public FlexWrap FlexWrap { get; set; } = FlexWrap.NoWrap;
    public JustifyContent JustifyContent { get; set; } = JustifyContent.FlexStart;
    public AlignItems AlignItems { get; set; } = AlignItems.Stretch;
    public float FlexGrow { get; set; } = 0f;
    public float FlexShrink { get; set; } = 1f;
    public float? FlexBasis { get; set; }
    public float? Gap { get; set; }

    // Z-index / stacking
    public int ZIndex { get; set; }
    public float Opacity { get; set; } = 1f;

    // Cursor
    public string Cursor { get; set; } = "default";

    // CSS Custom Properties (--variable-name: value)
    // Inherited down the DOM tree by LayoutEngine.BuildBox
    public Dictionary<string, string> CssVariables { get; set; } = [];

    // CSS Transitions
    public List<TransitionSpec> Transitions { get; set; } = [];
}
