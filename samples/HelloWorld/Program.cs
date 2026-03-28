using Mio.App;

var app = MioApplication.CreateBuilder(args)
    .WithTitle("MioSharp")
    .WithSize(1280, 720)
    .Build();

app.UseStaticFiles("Web");

// C# backend — callable from JS via window.mio.invoke('getHello')
app.MapInvoke("getHello", _ =>
    new { message = "Hello from C#!", timestamp = DateTime.UtcNow.ToString("o") });

app.MapInvoke("getTime", _ =>
    new { time = DateTime.Now.ToString("HH:mm:ss") });

app.LoadEntry("Web/index.html");
app.Run();
