namespace Mio.Core;

public sealed class WindowOptions
{
    public string Title { get; set; } = "MioSharp";
    public Size Size { get; set; } = new Size(1280, 720);
    public bool Resizable { get; set; } = true;
    public bool StartCentered { get; set; } = true;
}
