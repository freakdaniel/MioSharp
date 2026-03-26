using Mio.App;
using Mio.Routing;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("MioSharp — Hello World")
    .WithSize(1280, 720)
    .Build();

app.UseStaticFiles("Web");

// In-process API route — called by JS fetch('/api/hello') with NO HTTP server
app.MapGet("/api/hello", ctx =>
    ctx.Json(new { message = "Hello from C#!", timestamp = DateTime.UtcNow }));

app.LoadEntry("Web/index.html");
app.Run();
