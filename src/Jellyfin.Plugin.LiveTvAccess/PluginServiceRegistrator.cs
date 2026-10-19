using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.LiveTvAccess;

/// <summary>
/// Registers plugin services. Runs inside ApplicationHost.Init, before Startup.ConfigureServices
/// (Emby.Server.Implementations/ApplicationHost.cs:460-462, Jellyfin.Server/Program.cs:170).
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHostedService<StartupLogService>();
    }
}
