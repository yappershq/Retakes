using Microsoft.Extensions.Logging;
using Retakes.Plugins;

namespace Retakes.Modules;

/// <summary>
/// Trivial placeholder module — proves the IModule wiring compiles and logs a proof-of-life message.
/// Replace or remove when real modules (ConfigModule, PlayerLifecycleModule, …) are added in Phase B.
/// </summary>
internal sealed class BootstrapModule : IModule
{
    private readonly ILogger<BootstrapModule> _logger;

    public BootstrapModule(ILogger<BootstrapModule> logger) => _logger = logger;

    public bool Init()
    {
        _logger.LogInformation("[Retakes] Retakes loaded.");
        return true;
    }

    public void OnPostInit()           { }
    public void OnAllSharpModulesLoaded() { }
    public void Shutdown()             { }
}
