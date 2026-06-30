using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Retakes.Zones;

/// <summary>JSON deserialization shapes for the Retakes-Zones zone file format.</summary>
public class JsonBombsiteZones
{
    public JsonZone[] a { get; init; } = [];
    public JsonZone[] b { get; init; } = [];
}

/// <summary>A single zone entry from the JSON file.</summary>
public class JsonZone
{
    public int     type  { get; set; }
    public int[]   teams { get; set; } = [];

    /// <summary>Corner vector 1: [x, y, z]</summary>
    public float[] x { get; set; } = [];

    /// <summary>Corner vector 2: [x, y, z]</summary>
    public float[] y { get; set; } = [];
}

public enum ZoneType
{
    Green = 0,
    Red   = 1,
}

/// <summary>A resolved, axis-aligned bounding-box zone.</summary>
public class ZoneData
{
    public ZoneType Type;
    public int[]    Teams = [];
    public float    MinX, MinY, MinZ;
    public float    MaxX, MaxY, MaxZ;

    /// <summary>
    /// Returns true if the player position (px, py, pz) is inside this zone.
    /// A 36-unit vertical offset is added to the player Z so the check uses waist height.
    /// </summary>
    public bool IsInZone(float px, float py, float pz)
        => px >= MinX && px <= MaxX
        && py >= MinY && py <= MaxY
        && pz + 36 >= MinZ && pz + 36 <= MaxZ;
}

/// <summary>Per-player zone tracking state (keyed by SteamID64 in ZonesModule).</summary>
public class PlayerZoneState
{
    public List<ZoneData> Zones      = [];
    public List<ZoneData> GreenZones = [];
}
