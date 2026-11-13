using System.Text.Json;

namespace FatzMedia.Video.WebView2;

/// <summary>What the player page reports to the host (see <c>youtube-player.html</c>).</summary>
enum YouTubePlayerMessageType
{
    Unknown = 0,
    Ready,
    State,
    Time,
    Rate,
    Volume,
    Error,
    LoadFailed,
}

/// <summary>One message from the player page, parsed from JSON. Pure logic, no web view.</summary>
sealed record YouTubePlayerMessage
{
    public YouTubePlayerMessageType Type { get; init; }

    /// <summary>Position in seconds (state and time messages).</summary>
    public double? Seconds { get; init; }

    public double? DurationSeconds { get; init; }

    /// <summary>YouTube's player state code (-1, 0, 1, 2, 3, 5).</summary>
    public int? StateCode { get; init; }

    /// <summary>YouTube's volume, 0 to 100.</summary>
    public double? Volume { get; init; }

    public bool? Muted { get; init; }

    public double? Rate { get; init; }

    public IReadOnlyList<double> Rates { get; init; } = [];

    /// <summary>YouTube's error code.</summary>
    public int? ErrorCode { get; init; }

    /// <summary>Parses a message; unknown or malformed input becomes <see cref="YouTubePlayerMessageType.Unknown"/>.</summary>
    public static YouTubePlayerMessage Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeProperty))
                return new YouTubePlayerMessage();

            var type = typeProperty.GetString() switch
            {
                "ready" => YouTubePlayerMessageType.Ready,
                "state" => YouTubePlayerMessageType.State,
                "time" => YouTubePlayerMessageType.Time,
                "rate" => YouTubePlayerMessageType.Rate,
                "volume" => YouTubePlayerMessageType.Volume,
                "error" => YouTubePlayerMessageType.Error,
                "loadfailed" => YouTubePlayerMessageType.LoadFailed,
                _ => YouTubePlayerMessageType.Unknown,
            };

            return new YouTubePlayerMessage
            {
                Type = type,
                Seconds = Number(root, "t"),
                DurationSeconds = Number(root, "dur"),
                StateCode = Number(root, "state") is { } s ? (int)s : null,
                Volume = Number(root, "vol"),
                Muted = root.TryGetProperty("muted", out var m) && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.GetBoolean() : null,
                Rate = Number(root, "rate"),
                Rates = Numbers(root, "rates"),
                ErrorCode = Number(root, "code") is { } c ? (int)c : null,
            };
        }
        catch (JsonException)
        {
            return new YouTubePlayerMessage();
        }
    }

    /// <summary>Maps YouTube's state code to <see cref="WebMediaState"/>.</summary>
    public static WebMediaState MapState(int code) => code switch
    {
        0 => WebMediaState.Ended,
        1 => WebMediaState.Playing,
        2 => WebMediaState.Paused,
        3 => WebMediaState.Buffering,
        5 => WebMediaState.Cued,
        _ => WebMediaState.Unstarted,
    };

    /// <summary>Builds the command message the page understands.</summary>
    public static string Command(string cmd, double? value = null) =>
        value is null
            ? JsonSerializer.Serialize(new { cmd })
            : JsonSerializer.Serialize(new { cmd, value });

    static double? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    static List<double> Numbers(JsonElement root, string name)
    {
        var list = new List<double>();
        if (root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out var d))
                    list.Add(d);
            }
        }

        return list;
    }
}
