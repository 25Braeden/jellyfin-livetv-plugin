using Jellyfin.Data.Events.Users;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
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
        // One worker instance: hosted (start/stop) and injectable as ISyncSignals (filter, event consumers).
        serviceCollection.AddSingleton<TagSyncService>();
        serviceCollection.AddSingleton<ISyncSignals>(sp => sp.GetRequiredService<TagSyncService>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<TagSyncService>());

        // Jellyfin resolves event consumers from DI (Jellyfin.Server.Implementations/Events/EventManager.cs:46-52).
        serviceCollection.AddScoped<IEventConsumer<UserCreatedEventArgs>, UserEventsConsumer>();
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, UserEventsConsumer>();

        // Global MVC action filter. Options configuration is order-independent, so this applies even though
        // Jellyfin's own AddMvc runs later (Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:123).
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<LiveTvAccessFilter>());
    }
}
