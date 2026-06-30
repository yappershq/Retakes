using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Retakes.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace Retakes.Spawn;

/// <summary>Converts <see cref="Vector"/> to/from "X Y Z" JSON string (CSS retakes format).</summary>
public sealed class VectorJsonConverter : JsonConverter<Vector>
{
    public override Vector Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString() ?? "0 0 0";
        var parts = s.Split(' ');
        if (parts.Length < 3) return new Vector();
        float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x);
        float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y);
        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var z);
        return new Vector(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, Vector value, JsonSerializerOptions options)
        => writer.WriteStringValue(
            string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{value.X} {value.Y} {value.Z}"));
}

public class Spawn
{
    [JsonPropertyName("Vector")]
    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector Position { get; set; }

    [JsonPropertyName("QAngle")]
    [JsonConverter(typeof(VectorJsonConverter))]
    public Vector Angles { get; set; }

    [JsonPropertyName("Team")]
    public CStrikeTeam Team { get; set; }

    [JsonPropertyName("Bombsite")]
    public Bombsite Bombsite { get; set; }

    [JsonPropertyName("CanBePlanter")]
    public bool CanBePlanter { get; set; }
}
