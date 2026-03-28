using System.Text.Json;
using Mio.App;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("HelloDefault — MioSharp")
    .WithSize(1280, 720)
    .Build();

app.UseStaticFiles("Web");

// All C# backend communication goes through window.mio.invoke — never via fetch()
app.MapInvoke("getTime", _ =>
    new { time = DateTime.Now.ToString("HH:mm:ss"), date = DateTime.Now.ToString("yyyy-MM-dd") });

app.MapInvoke("greet", args =>
{
    var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n)
        ? n.GetString() ?? "World"
        : "World";
    return new { message = $"Hello, {name}! Greetings from C#." };
});

app.MapInvoke("getPlatformInfo", _ =>
    new
    {
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
    });

app.LoadEntry("Web/index.html");
app.Run();
