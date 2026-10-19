using Jellyfin.Plugin.LiveTvAccess.Filters;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
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
        serviceCollection.AddSingleton<AccessService>();
        serviceCollection.AddHostedService<TagSyncService>();

        // Global MVC action filter. Options configuration is order-independent, so this applies even though
        // Jellyfin's own AddMvc runs later (Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:123).
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<LiveTvAccessFilter>());
    }
}
