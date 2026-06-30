namespace Retakes.Plugins;

/// <summary>
/// Internal module contract used inside Retakes.Core's DI container to fan the
/// ModSharp lifecycle out to every cooperating service.
/// The plugin entry walks all <see cref="IModule"/> instances on each lifecycle phase.
/// </summary>
public interface IModule
{
    /// <summary>Called from the plugin's <c>Init()</c>. Return false to abort load.</summary>
    bool Init();

    /// <summary>Called from the plugin's <c>PostInit()</c> — register published interfaces here.</summary>
    void OnPostInit();

    /// <summary>Called from the plugin's <c>OnAllModulesLoaded()</c> — resolve cross-plugin interfaces here.</summary>
    void OnAllSharpModulesLoaded();

    /// <summary>Called from the plugin's <c>Shutdown()</c>.</summary>
    void Shutdown();
}
