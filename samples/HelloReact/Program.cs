using Mio.App;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("HelloReact — MioSharp")
    .WithSize(1280, 720)
    .Build();

app.UseStaticFiles("dist");

app.MapInvoke("getAppInfo", _ =>
    new
    {
        name    = "HelloReact",
        version = "1.0.0",
        engine  = "MioSharp",
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
    });

app.MapInvoke("getClock", _ =>
    new { time = DateTime.Now.ToString("HH:mm:ss"), timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

app.LoadEntry("dist/index.html");
app.Run();
