using System.Text.Json;
using Mio.App;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("HelloTS — MioSharp")
    .WithSize(1280, 720)
    .Build();

app.UseStaticFiles("Web");

app.MapInvoke("getServerTime", _ =>
    new { iso = DateTime.UtcNow.ToString("o"), local = DateTime.Now.ToString("HH:mm:ss") });

app.MapInvoke("compute", args =>
{
    var a = args.TryGetProperty("a", out var av) ? av.GetDouble() : 0;
    var b = args.TryGetProperty("b", out var bv) ? bv.GetDouble() : 0;
    return new { sum = a + b, product = a * b, power = Math.Pow(a, b) };
});

app.LoadEntry("Web/index.html");
app.Run();
