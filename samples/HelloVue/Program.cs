using Mio.App;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("HelloVue — MioSharp")
    .WithSize(1280, 720)
    .Build();

// Vue build output is served from dist/
app.UseStaticFiles("dist");

// C# backend exposed to Vue components via window.mio.invoke
app.MapInvoke("getStats", _ =>
    new
    {
        uptime  = (int)Environment.TickCount64 / 1000,
        memory  = GC.GetTotalMemory(false) / 1024 / 1024,
        version = "1.0.0"
    });

app.MapInvoke("ping", _ => new { pong = true, ts = DateTime.UtcNow.ToString("o") });

app.LoadEntry("dist/index.html");
app.Run();
