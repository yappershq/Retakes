using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;

namespace Retakes.Config;

internal sealed class ConfigModule : IModule
{
    private readonly InterfaceBridge       _bridge;
    private readonly ILogger<ConfigModule> _logger;

    public RetakesConfig Config { get; private set; } = new();

    public ConfigModule(InterfaceBridge bridge, ILogger<ConfigModule> logger)
    {
        _bridge = bridge;
        _logger = logger;
    }

    public bool Init()
    {
        var path = Path.Combine(_bridge.ConfigPath, "retakes.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };

        if (!File.Exists(path))
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Config, opts));
        }
        else
        {
            var text = File.ReadAllText(path);
            Config = JsonSerializer.Deserialize<RetakesConfig>(text, opts) ?? new RetakesConfig();
        }

        if (Config.Database.ConnectionString.Contains("CHANGE_ME", StringComparison.Ordinal))
            _logger.LogWarning("[Retakes] Database password is still CHANGE_ME — update configs/retakes/retakes.json before use.");

        return true;
    }

    public void OnPostInit()              { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown()                { }
}
