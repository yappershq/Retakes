using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Retakes.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace Retakes.Spawn;

internal sealed class MapConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters    = { new VectorJsonConverter() },
    };

    private readonly ILogger<MapConfigService> _logger;
    private readonly string                    _mapConfigDir;
    private          string                    _mapName = "unknown";
    private          MapConfigData             _data    = new();

    public MapConfigService(ILogger<MapConfigService> logger, string dataPath)
    {
        _logger       = logger;
        _mapConfigDir = Path.Combine(dataPath, "map_config");
        Directory.CreateDirectory(_mapConfigDir);
    }

    public void LoadForMap(string mapName)
    {
        _mapName = mapName;
        _data    = new MapConfigData();

        var path = MapFilePath(mapName);
        if (!File.Exists(path))
        {
            _logger.LogInformation("[Retakes] No spawn config for {Map}, starting fresh.", mapName);
            return;
        }

        try
        {
            var text = File.ReadAllText(path);
            _data = JsonSerializer.Deserialize<MapConfigData>(text, JsonOpts) ?? new MapConfigData();
            _logger.LogInformation("[Retakes] Loaded {Count} spawns for {Map}.", _data.Spawns.Count, mapName);
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "[Retakes] Failed to load spawn config for {Map}.", mapName);
        }
    }

    public void Save()
    {
        var path = MapFilePath(_mapName);
        var sanitized = GetSanitizedMapConfigData();
        File.WriteAllText(path, JsonSerializer.Serialize(sanitized, JsonOpts));
    }

    /// <summary>Add a spawn, deduplicating by (position, bombsite).</summary>
    public bool AddSpawn(Spawn spawn)
    {
        if (IsDuplicate(spawn.Position, spawn.Bombsite))
            return false;
        _data.Spawns.Add(spawn);
        Save();
        return true;
    }

    public bool RemoveSpawn(Spawn spawn)
    {
        var idx = _data.Spawns.FindIndex(s =>
            s.Position.DistToSqr(spawn.Position) < 0.01f && s.Bombsite == spawn.Bombsite);
        if (idx < 0) return false;
        _data.Spawns.RemoveAt(idx);
        Save();
        return true;
    }

    public List<Spawn> GetSpawnsClone() => [.. _data.Spawns];

    public IReadOnlyList<Spawn> GetSpawnsByTeamAndSite(CStrikeTeam team, Bombsite site)
        => _data.Spawns.Where(s => s.Team == team && s.Bombsite == site).ToList();

    // ── internal helpers ────────────────────────────────────────────────────

    private bool IsDuplicate(Vector position, Bombsite site)
        => _data.Spawns.Any(s => s.Position.DistToSqr(position) < 0.01f && s.Bombsite == site);

    private MapConfigData GetSanitizedMapConfigData()
    {
        // remove positional duplicates, keep first
        var seen = new List<(Vector pos, Bombsite site)>();
        var result = new MapConfigData();
        foreach (var s in _data.Spawns)
        {
            if (seen.Any(x => x.pos.DistToSqr(s.Position) < 0.01f && x.site == s.Bombsite))
                continue;
            seen.Add((s.Position, s.Bombsite));
            result.Spawns.Add(s);
        }
        return result;
    }

    private string MapFilePath(string mapName)
        => Path.Combine(_mapConfigDir, $"{mapName}.json");
}
