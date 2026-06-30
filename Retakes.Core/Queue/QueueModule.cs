using Microsoft.Extensions.Logging;
using Retakes.Config;
using Retakes.Plugins;
using Sharp.Modules.AdminManager.Shared;

namespace Retakes.Queue;

internal sealed class QueueModule : IModule
{
    private readonly InterfaceBridge       _bridge;
    private readonly ILogger<QueueModule>  _logger;

    public QueueManager QueueManager { get; private set; } = null!;
    public GameManager  GameManager  { get; private set; } = null!;

    public QueueModule(InterfaceBridge bridge, ILogger<QueueModule> logger, ConfigModule config)
    {
        _bridge = bridge;
        _logger = logger;

        // create eagerly so PlayerLifecycleModule can DI-inject QueueModule and access them
        QueueManager = new QueueManager(_bridge.LoggerFactory.CreateLogger<QueueManager>(), bridge, config);
        GameManager  = new GameManager(_bridge.LoggerFactory.CreateLogger<GameManager>(),  bridge, config, QueueManager);
    }

    public bool Init() => true;

    public void OnPostInit() { }

    public void OnAllSharpModulesLoaded()
    {
        var adminMgrIface = _bridge.SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);

        var adminMgr = adminMgrIface?.Instance;
        QueueManager.SetAdminManager(adminMgr);

        if (adminMgr is null)
            _logger.LogInformation("[Retakes] AdminManager not available — queue priority disabled.");
        else
            _logger.LogInformation("[Retakes] AdminManager resolved for queue priority.");
    }

    public void Shutdown() { }
}
