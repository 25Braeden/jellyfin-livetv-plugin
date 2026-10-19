using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveTvAccess;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using LiveTvAccess.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>Interface fake: unhandled members return default (completed Task for async).</summary>
public class Fake<T> : DispatchProxy
    where T : class
{
    public Dictionary<string, Func<object?[], object?>> Handlers { get; } = new();

    public static (T Instance, Fake<T> Control) Create()
    {
        var instance = Create<T, Fake<T>>();
        return (instance, (Fake<T>)(object)instance);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is null)
        {
            return null;
        }

        if (Handlers.TryGetValue(targetMethod.Name, out var handler))
        {
            return handler(args ?? []);
        }

        var rt = targetMethod.ReturnType;
        if (rt == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = rt.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                .Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
        }

        return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
    }
}

/// <summary>A small world: plugin + config, users, channels, and the services under test.</summary>
public sealed class World
{
    public const string Tuner1Url = "/tuners/tuner1.m3u";
    public const string Tuner2Url = "/tuners/tuner2.m3u";

    public World()
    {
        var (paths, pathsFake) = Fake<IApplicationPaths>.Create();
        var dir = Path.Combine(Path.GetTempPath(), "livetvaccess-tests");
        pathsFake.Handlers["get_PluginConfigurationsPath"] = _ => dir;
        pathsFake.Handlers["get_PluginsPath"] = _ => dir;
        var (xml, _) = Fake<IXmlSerializer>.Create();
        var (users, usersFake) = Fake<IUserManager>.Create();
        UsersFake = usersFake;
        usersFake.Handlers["GetUserById"] = args => Users.FirstOrDefault(u => u.Id == (Guid)args[0]!);
        usersFake.Handlers["GetUserByName"] = args =>
            Users.FirstOrDefault(u => string.Equals(u.Username, (string)args[0]!, StringComparison.OrdinalIgnoreCase));
        usersFake.Handlers["GetUsers"] = _ => Users.ToList();

        Plugin = new Plugin(paths, xml, NullLogger<Plugin>.Instance, users);

        UserA = AddUser("A");
        UserB = AddUser("B");
        UserC = AddUser("C");
        Admin = AddUser("admin");
        Admin.SetPermission(PermissionKind.IsAdministrator, true);

        Tuner1Channel = AddChannel(Tuner1Url, "stream-a");
        Tuner2Channel = AddChannel(Tuner2Url, "stream-b");

        var (library, libraryFake) = Fake<ILibraryManager>.Create();
        LibraryFake = libraryFake;
        libraryFake.Handlers["GetItemById"] = args =>
        {
            if (args[0] is not Guid id)
            {
                return null;
            }

            var item = Items.GetValueOrDefault(id);
            OnGetItemById?.Invoke(id);
            return item;
        };
        libraryFake.Handlers["GetItemList"] = _ => (IReadOnlyList<BaseItem>)Items.Values.ToList();

        var (mediaSources, msFake) = Fake<IMediaSourceManager>.Create();
        MediaSourcesFake = msFake;
        var (config, configFake) = Fake<IConfigurationManager>.Create();
        configFake.Handlers["GetConfiguration"] = _ => new LiveTvOptions();

        LibraryManager = library;
        UserManager = users;
        Access = new AccessService(library, mediaSources, users, config, NullLogger<AccessService>.Instance);
        SetConfig(DefaultConfig());
    }

    public Plugin Plugin { get; }

    public List<User> Users { get; } = new();

    public Dictionary<Guid, BaseItem> Items { get; } = new();

    public Fake<ILibraryManager> LibraryFake { get; }

    public Fake<IUserManager> UsersFake { get; }

    public Fake<IMediaSourceManager> MediaSourcesFake { get; }

    public ILibraryManager LibraryManager { get; }

    public IUserManager UserManager { get; }

    public AccessService Access { get; }

    public Action<Guid>? OnGetItemById { get; set; }

    public User UserA { get; }

    public User UserB { get; }

    public User UserC { get; }

    public User Admin { get; }

    public LiveTvChannel Tuner1Channel { get; }

    public LiveTvChannel Tuner2Channel { get; }

    public static PluginConfiguration DefaultConfig() => new()
    {
        Tuners = [new TunerConfig { Key = "tuner1", Url = Tuner1Url }, new TunerConfig { Key = "tuner2", Url = Tuner2Url }],
        Users = [new UserTunerConfig { UserName = "A", TunerKey = "tuner1" }, new UserTunerConfig { UserName = "B", TunerKey = "tuner2" }],
    };

    public void SetConfig(PluginConfiguration config)
    {
        Plugin.UpdateConfiguration(config);
        Access.Invalidate();
    }

    public User AddUser(string name)
    {
        var user = new User(name, "auth", "reset");
        user.AddDefaultPermissions();
        Users.Add(user);
        return user;
    }

    public LiveTvChannel AddChannel(string tunerUrl, string streamUrl)
    {
        var channel = new LiveTvChannel
        {
            Id = Guid.NewGuid(),
            Name = streamUrl,
            ExternalId = JellyfinIds.M3uChannelPrefix(tunerUrl) + JellyfinIds.Md5Guid(streamUrl),
        };
        Items[channel.Id] = channel;
        return channel;
    }

    public static ClaimsPrincipal Principal(Guid? userId, bool isApiKey = false, bool authenticated = true)
    {
        var claims = new List<Claim>();
        if (userId is { } id)
        {
            claims.Add(new Claim("Jellyfin-UserId", id.ToString("N")));
        }

        claims.Add(new Claim("Jellyfin-IsApiKey", isApiKey.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "CustomAuthentication" : null));
    }

    public static ClaimsPrincipal PrincipalWithoutUserClaim()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "someone")], "CustomAuthentication"));

    public static ActionExecutingContext Context(ClaimsPrincipal principal, IDictionary<string, object?> arguments)
    {
        var http = new DefaultHttpContext { User = principal };
        http.Request.Method = "POST";
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(action, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    /// <summary>Runs the filter; returns the short-circuit status code, or null if the action ran.</summary>
    public async Task<int?> RunFilter(ClaimsPrincipal principal, IDictionary<string, object?> arguments)
    {
        var filter = new Filters.LiveTvAccessFilter(Access, NullLogger<Filters.LiveTvAccessFilter>.Instance);
        var context = Context(principal, arguments);
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
        });
        return ran ? null : (context.Result as ContentResult)?.StatusCode ?? -1;
    }
}
