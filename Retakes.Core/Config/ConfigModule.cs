using System.IO;
using System.Text.Json;
using Retakes.Plugins;

namespace Retakes.Config;

internal sealed class ConfigModule : IModule
{
    private readonly InterfaceBridge _bridge;

    public RetakesConfig Config { get; private set; } = new();

    public ConfigModule(InterfaceBridge bridge) => _bridge = bridge;

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

        return true;
    }

    public void OnPostInit()              { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown()                { }
}
