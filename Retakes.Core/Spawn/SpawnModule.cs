using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Sharp.Shared.Listeners;

namespace Retakes.Spawn;

internal sealed class SpawnModule : IModule, IGameListener
{
    private readonly InterfaceBridge          _bridge;
    private readonly ILogger<SpawnModule>     _logger;
    private readonly ConfigModule             _config;

    public MapConfigService MapConfig    { get; private set; } = null!;
    public SpawnManager     SpawnManager { get; private set; } = null!;

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    public SpawnModule(InterfaceBridge bridge, ILogger<SpawnModule> logger, ConfigModule config)
    {
        _bridge = bridge;
        _logger = logger;
        _config = config;
    }

    public bool Init()
    {
        MapConfig    = new MapConfigService(_bridge.LoggerFactory.CreateLogger<MapConfigService>(), _bridge.DataPath);
        SpawnManager = new SpawnManager(_bridge.LoggerFactory.CreateLogger<SpawnManager>(), _bridge);

        var mapName = _bridge.ModSharp.GetMapName() ?? "unknown";
        LoadForMap(mapName);

        return true;
    }

    public void OnPostInit()
        => _bridge.ModSharp.InstallGameListener(this);

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown()
        => _bridge.ModSharp.RemoveGameListener(this);

    // ── IGameListener ──────────────────────────────────────────────────────

    void IGameListener.OnServerActivate()
    {
        var mapName = _bridge.ModSharp.GetMapName() ?? "unknown";
        _logger.LogInformation("[Retakes] Map changed to {Map}, reloading spawns.", mapName);
        LoadForMap(mapName);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private void LoadForMap(string mapName)
    {
        MapConfig.LoadForMap(mapName);
        SpawnManager.Rebuild(MapConfig);
    }
}
