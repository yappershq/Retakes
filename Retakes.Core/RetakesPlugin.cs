using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retakes.Plugins;
using Retakes.Shared;
using Sharp.Shared;

namespace Retakes;

/// <summary>
/// Retakes — ModSharp port of b3none/cs2-retakes + yonilerner/cs2-retakes-allocator + Retakes-Zones + RetakeDefuseFix.
///
/// Lifecycle (honours ModSharp "all PostInits finish before any OAM" guarantee):
///   PostInit           — publish IRetakesService so external plugins can subscribe in their OAM.
///   OnAllModulesLoaded — resolve optional external interfaces (e.g. CommandCenter).
/// </summary>
public sealed class RetakesPlugin : IModSharpModule
{
    public string DisplayName   => "Retakes";
    public string DisplayAuthor => "yappershq";

    private readonly IServiceProvider        _provider;
    private readonly ILogger<RetakesPlugin>  _logger;
    private readonly InterfaceBridge         _bridge;
    private readonly EventBus                _bus;

    public RetakesPlugin(
        ISharedSystem   sharedSystem,
        string?         dllPath,
        string?         sharpPath,
        Version?        version,
        IConfiguration? coreConfiguration,
        bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(sharedSystem);
        ArgumentNullException.ThrowIfNull(sharpPath);

        var loggerFactory = sharedSystem.GetLoggerFactory();
        _bridge = new InterfaceBridge(this, sharedSystem, sharpPath, loggerFactory);
        _bus    = new EventBus();

        var services = new ServiceCollection();
        services.AddSingleton(sharedSystem);
        services.AddSingleton(_bridge);
        services.AddSingleton(_bus);
        services.AddSingleton<ILoggerFactory>(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(LoggerFactoryLogger<>));
        services.AddModules();

        _provider = services.BuildServiceProvider();
        _logger   = _provider.GetRequiredService<ILogger<RetakesPlugin>>();
    }

    public bool Init()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => { m.Init(); }, "Init");
        return true;
    }

    /// <summary>Publish IRetakesService so external plugins can subscribe in their OAM.</summary>
    public void PostInit()
    {
        _bridge.SharpModuleManager
            .RegisterSharpModuleInterface<IRetakesService>(this, IRetakesService.Identity, _bus);

        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnPostInit(), "PostInit");

        _logger.LogInformation("[Retakes] Published IRetakesService.");
    }

    public void OnAllModulesLoaded()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.OnAllSharpModulesLoaded(), "OnAllModulesLoaded");

        _logger.LogInformation("[Retakes] All modules loaded.");
    }

    public void Shutdown()
    {
        foreach (var module in _provider.GetServices<IModule>())
            CallSafe(module, static m => m.Shutdown(), "Shutdown");

        if (_provider is IDisposable disposable)
            disposable.Dispose();
    }

    private void CallSafe(IModule module, Action<IModule> action, string phase)
    {
        try   { action(module); }
        catch (Exception ex) { _logger.LogError(ex, "[Retakes] Error in {Phase} for {Module}", phase, module.GetType().Name); }
    }
}

/// <summary>Generic logger adapter bridging ILogger&lt;T&gt; onto ModSharp's factory.</summary>
internal sealed class LoggerFactoryLogger<T>(ILoggerFactory factory) : ILogger<T>
{
    private readonly ILogger _inner = factory.CreateLogger(typeof(T).Name);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
