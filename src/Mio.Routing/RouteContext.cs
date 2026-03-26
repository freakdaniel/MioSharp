using System.Text;
using System.Text.Json;

namespace Mio.Routing;

public sealed class RouteContext
{
    public string Path { get; }
    public string Method { get; }
    public Dictionary<string, string> Query { get; } = [];
    public byte[]? Body { get; internal set; }

    internal RouteResponse? Response { get; private set; }

    public RouteContext(string method, string path)
    {
        Method = method.ToUpperInvariant();
        Path = path;
    }

    public RouteResponse Json<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
        Response = new RouteResponse(200, "application/json", Encoding.UTF8.GetBytes(json));
        return Response;
    }

    public RouteResponse Ok(string content = "")
    {
        Response = new RouteResponse(200, "text/plain", Encoding.UTF8.GetBytes(content));
        return Response;
    }

    public RouteResponse NotFound()
    {
        Response = new RouteResponse(404, "text/plain", "Not Found"u8.ToArray());
        return Response;
    }

    public async Task<T?> ReadBodyAsync<T>()
    {
        if (Body == null) return default;
        using var ms = new MemoryStream(Body);
        return await JsonSerializer.DeserializeAsync<T>(ms, JsonSerializerOptions.Web);
    }
}

public sealed class RouteResponse(int status, string contentType, byte[] body)
{
    public int Status { get; } = status;
    public string ContentType { get; } = contentType;
    public byte[] Body { get; } = body;

    public string BodyText => System.Text.Encoding.UTF8.GetString(Body);
}
