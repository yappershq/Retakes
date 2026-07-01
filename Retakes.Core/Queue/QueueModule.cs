using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;

namespace Retakes.Queue;

internal sealed class QueueModule : IModule
{
    private readonly InterfaceBridge       _bridge;

    public QueueManager QueueManager { get; private set; } = null!;
    public GameManager  GameManager  { get; private set; } = null!;

    public QueueModule(InterfaceBridge bridge, ILogger<QueueModule> logger, ConfigModule config)
    {
        _bridge = bridge;

        // create eagerly so PlayerLifecycleModule can DI-inject QueueModule and access them
        QueueManager = new QueueManager(_bridge.LoggerFactory.CreateLogger<QueueManager>(), bridge, config);
        GameManager  = new GameManager(_bridge.LoggerFactory.CreateLogger<GameManager>(),  bridge, config, QueueManager);
    }

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded() { }

    public void Shutdown() { }
}
