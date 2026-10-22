using System.Text.Json;
using System.Text.Json.Serialization;

namespace EwCapture.Core;

public sealed record Size2(int Width, int Height);

public sealed record MonitorInfo(int Width, int Height, double DpiScale);

public sealed record GameInfo(
    string Process,
    string WindowMode,
    Dictionary<string, Dictionary<string, string>> Config);

/// <summary>Sent with every capture; the server stores it as given.</summary>
public sealed record CaptureMeta(
    string Tool,
    DateTimeOffset CapturedAt,
    string? Label,
    Size2 Client,
    MonitorInfo Monitor,
    GameInfo Game)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
