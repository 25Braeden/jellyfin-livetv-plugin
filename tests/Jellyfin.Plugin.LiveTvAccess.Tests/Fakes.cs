using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.LiveTvAccess;
using Jellyfin.Plugin.LiveTvAccess.Configuration;
using Jellyfin.Plugin.LiveTvAccess.Health;
using LiveTvAccess.Core;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
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

/// <summary>Logger that keeps formatted messages (for asserting on what the plugin reports).</summary>
public sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Messages.Enqueue($"{logLevel}: {formatter(state, exception)}");
}

/// <summary>Signals stub for filter tests that don't involve the sync worker.</summary>
public sealed class StaticSelfCheck : ISelfCheck
{
    public StaticSelfCheck(bool healthy) => IsHealthy = healthy;

    public bool IsHealthy { get; set; }

    public SelfCheckStatus Status => new() { State = IsHealthy ? SelfCheckStatus.Healthy : SelfCheckStatus.Failed };

    public Task<SelfCheckStatus> RunAsync(string trigger, CancellationToken cancellationToken) => Task.FromResult(Status);
}

public sealed class ReadySignals : ISyncSignals
{
    public bool IsReady { get; set; } = true;

    public int UsersDirty { get; private set; }

    public void MarkUsersDirty(string reason) => UsersDirty++;

    public void MarkChannelsDirty(string reason)
    {
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
        usersFake.Handlers["UpdateUserAsync"] = args =>
        {
            Interlocked.Increment(ref _userUpdates);
            OnUpdateUser?.Invoke((User)args[0]!);
            return Task.CompletedTask;
        };

        Plugin = new Plugin(paths, xml, NullLogger<Plugin>.Instance, users);

        UserA = AddUser("A");
        UserB = AddUser("B");
        UserC = AddUser("C");
        Admin = AddUser("admin");
        Admin.SetPermission(PermissionKind.IsAdministrator, true);

        Tuner1Channel = AddChannel(Tuner1Url, "stream-a");
        Tuner2Channel = AddChannel(Tuner2Url, "stream-b");
        Tuner1Program = AddItem(new LiveTvProgram { ChannelId = Tuner1Channel.Id, Name = "p1" });
        Tuner2Program = AddItem(new LiveTvProgram { ChannelId = Tuner2Channel.Id, Name = "p2" });
        Movie = AddItem(new MediaBrowser.Controller.Entities.Movies.Movie { Name = "Test Movie", Path = "/media/library/movies/m.mp4" });
        Song = AddItem(new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Tone", Path = "/media/library/music/t.mp3" });
        LibraryFolder = AddItem(new Folder { Name = "Movies", Path = "/media/library/movies" });
        Recording = AddItem(new Video { Name = "Recorded show", Path = "/recordings/show/episode.ts" });
        LiveStreams[Tuner1StreamId] = "host1";
        LiveStreams[Tuner2StreamId] = "host2";
        LiveStreams[OtherProviderStreamId] = null;

        var (library, libraryFake) = Fake<ILibraryManager>.Create();
        LibraryFake = libraryFake;
        libraryFake.Handlers["GetItemById"] = args =>
        {
            if (args[0] is not Guid id)
            {
                return null;
            }

            // Like Jellyfin: the empty id throws (LibraryManager.GetItemById), and some stored rows can't be
            // deserialized (e.g. the placeholder row 00000000000000000000000000000001).
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Guid can't be empty", "id");
            }

            if (ThrowingIds.Contains(id))
            {
                throw new InvalidOperationException("Cannot deserialize unknown type.");
            }

            var item = Items.GetValueOrDefault(id);
            OnGetItemById?.Invoke(id);
            return item;
        };
        libraryFake.Handlers["GetItemList"] = args =>
        {
            var query = (InternalItemsQuery)args[0]!;
            OnGetItemList?.Invoke(query);
            return (IReadOnlyList<BaseItem>)Items.Values
                .Where(i => query.ItemIds.Length == 0 || query.ItemIds.Contains(i.Id))
                .ToList();
        };
        libraryFake.Handlers["GetItemIds"] = _ =>
        {
            OnGetItemIds?.Invoke();
            return (IReadOnlyList<Guid>)Items.Values.OfType<LiveTvChannel>().Select(i => i.Id).ToList();
        };

        var (mediaSources, msFake) = Fake<IMediaSourceManager>.Create();
        MediaSourcesFake = msFake;
        pathsFake.Handlers["get_DataPath"] = _ => "/data";
        msFake.Handlers["GetLiveStreamInfo"] = args =>
        {
            if (args[0] is not string id || !LiveStreams.TryGetValue(id, out var hostId))
            {
                return null;
            }

            var (stream, streamFake) = Fake<MediaBrowser.Controller.Library.ILiveStream>.Create();
            streamFake.Handlers["get_TunerHostId"] = _ => hostId;
            streamFake.Handlers["get_EnableStreamSharing"] = _ => true;
            return stream;
        };
        var (config, configFake) = Fake<IConfigurationManager>.Create();
        configFake.Handlers["GetConfiguration"] = args => (string)args[0]! == "encoding"
            ? new MediaBrowser.Model.Configuration.EncodingOptions { TranscodingTempPath = TranscodePath }
            : LiveTv;
        configFake.Handlers["get_CommonApplicationPaths"] = _ => paths;
        ConfigurationManager = config;

        LibraryManager = library;

        // BaseItem.UpdateToRepositoryAsync goes through this static (set by Jellyfin at startup).
        BaseItem.LibraryManager = library;

        // Video.SourceType asks this static whether the file is an active recording (set by Jellyfin at startup).
        var (recordings, recordingsFake) = Fake<IRecordingsManager>.Create();
        recordingsFake.Handlers["GetActiveRecordingInfo"] = args => ActiveRecordingPaths.Contains((string)args[0]!) ? new ActiveRecordingInfo() : null;
        Video.RecordingsManager = recordings;
        UserManager = users;
        var (transcodes, transcodesFake) = Fake<MediaBrowser.Controller.MediaEncoding.ITranscodeManager>.Create();
        transcodesFake.Handlers["GetTranscodingJob"] = args => args.Length switch
        {
            1 when args[0] is string id && Jobs.TryGetValue(id, out var job) => job,
            2 when args[0] is string path => Jobs.Values.FirstOrDefault(j =>
                string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase) && Equals(j.Type, args[1])),
            _ => null,
        };
        Access = new AccessService(library, mediaSources, users, config, transcodes, NullLogger<AccessService>.Instance);
        SetConfig(DefaultConfig());
    }

    private int _userUpdates;

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

    public Action<InternalItemsQuery>? OnGetItemList { get; set; }

    public Action? OnGetItemIds { get; set; }

    public Action<User>? OnUpdateUser { get; set; }

    public int UserUpdates => Volatile.Read(ref _userUpdates);

    public ISyncSignals Signals { get; set; } = new ReadySignals();

    /// <summary>The self-check the filter consults (healthy by default).</summary>
    public ISelfCheck Health { get; set; } = new StaticSelfCheck(true);

    /// <summary>Jellyfin's Live TV configuration ("livetv"), mutable by tests.</summary>
    public LiveTvOptions LiveTv { get; } = new()
    {
        TunerHosts = [new TunerHostInfo { Id = "host1", Type = "m3u", Url = Tuner1Url }, new TunerHostInfo { Id = "host2", Type = "m3u", Url = Tuner2Url }],
        RecordingPath = "/recordings",
    };

    public IConfigurationManager ConfigurationManager { get; }

    public User UserA { get; }

    public User UserB { get; }

    public User UserC { get; }

    public User Admin { get; }

    public LiveTvChannel Tuner1Channel { get; }

    public LiveTvChannel Tuner2Channel { get; }

    public LiveTvProgram Tuner1Program { get; }

    public LiveTvProgram Tuner2Program { get; }

    public BaseItem Movie { get; }

    public BaseItem Song { get; }

    public BaseItem LibraryFolder { get; }

    public BaseItem Recording { get; }

    /// <summary>Ids whose lookup throws, like Jellyfin's placeholder row.</summary>
    public HashSet<Guid> ThrowingIds { get; } = [Guid.Parse("00000000000000000000000000000001")];

    /// <summary>Running transcoding jobs by play session id.</summary>
    public Dictionary<string, MediaBrowser.Controller.MediaEncoding.TranscodingJob> Jobs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a running transcoding job (optionally on a live stream).</summary>
    public string AddJob(string? liveStreamId)
    {
        var id = Guid.NewGuid().ToString("N");
        Jobs[id] = new MediaBrowser.Controller.MediaEncoding.TranscodingJob(NullLogger<MediaBrowser.Controller.MediaEncoding.TranscodingJob>.Instance)
        {
            PlaySessionId = id,
            LiveStreamId = liveStreamId,
            MediaSource = new MediaBrowser.Model.Dto.MediaSourceInfo { LiveStreamId = liveStreamId },
        };
        return id;
    }

    public const string TranscodePath = "/cache/transcodes";

    /// <summary>Adds a running HLS job with playlist name <paramref name="name"/> (optionally on a live stream).</summary>
    public string AddHlsJob(string name, string? liveStreamId)
    {
        var playSession = Guid.NewGuid().ToString("N");
        Jobs[playSession] = new MediaBrowser.Controller.MediaEncoding.TranscodingJob(NullLogger<MediaBrowser.Controller.MediaEncoding.TranscodingJob>.Instance)
        {
            PlaySessionId = playSession,
            Path = System.IO.Path.Combine(TranscodePath, name + ".m3u8"),
            Type = MediaBrowser.Controller.MediaEncoding.TranscodingJobType.Hls,
            LiveStreamId = liveStreamId,
            MediaSource = new MediaBrowser.Model.Dto.MediaSourceInfo { LiveStreamId = liveStreamId },
        };
        return name;
    }

    /// <summary>A legacy "params" string with <paramref name="value"/> in field <paramref name="index"/>.</summary>
    public static string Params(int index, string value)
        => string.Join(';', Enumerable.Range(0, Math.Max(index + 1, 29)).Select(i => i == index ? value : string.Empty));

    /// <summary>Paths reported as active recordings.</summary>
    public HashSet<string> ActiveRecordingPaths { get; } = new(StringComparer.Ordinal);

    /// <summary>Open live streams: id -> tuner host id (null for a non-tuner stream).</summary>
    public Dictionary<string, string?> LiveStreams { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly string Tuner1StreamId = $"{JellyfinIds.LiveTvProviderPrefix}_af999c25a00715699361240d4c6c7a53_4ddd999f02971aec37a820d93a68cb59";

    public static readonly string Tuner2StreamId = $"{JellyfinIds.LiveTvProviderPrefix}_af999c25a00715699361240d4c6c7a53_5ec6f96467952921c6e26b7f28e0b318";

    public static readonly string OtherProviderStreamId = "0123456789abcdef0123456789abcdef_somestream";

    public string TokenFor(BaseItem channel) => $"{JellyfinIds.LiveTvProviderPrefix}_LiveTvChannel_{channel.Id:N}_{JellyfinIds.Md5Guid(channel.Name)}";

    public T AddItem<T>(T item)
        where T : BaseItem
    {
        item.Id = Guid.NewGuid();
        Items[item.Id] = item;
        return item;
    }

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

    /// <summary>Creates the real sync worker against this world, with fast timings for tests.</summary>
    public TagSyncService CreateSync(Microsoft.Extensions.Logging.ILogger<TagSyncService>? logger = null)
    {
        var (tasks, _) = Fake<ITaskManager>.Create();
        var sync = new TagSyncService(Access, LibraryManager, UserManager, tasks, logger ?? NullLogger<TagSyncService>.Instance)
        {
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            PeriodicInterval = TimeSpan.FromHours(1),
            PageSize = 500,
        };
        Signals = sync;
        return sync;
    }

    /// <summary>Polls until the condition holds (or fails the test after the timeout).</summary>
    public static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < end)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("Timed out waiting for: " + what);
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

    public static ActionExecutingContext Context(
        ClaimsPrincipal principal,
        IDictionary<string, object?> arguments,
        string method = "POST",
        string? template = null,
        string? controller = null,
        string? action = null,
        params string[] routeArgs)
    {
        var http = new DefaultHttpContext { User = principal };
        http.Request.Method = method;
        ActionDescriptor descriptor = controller is null
            ? new ActionDescriptor()
            : new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { ControllerName = controller, ActionName = action ?? string.Empty };
        descriptor.AttributeRouteInfo = template is null ? null : new AttributeRouteInfo { Template = template };
        var routeData = new RouteData();
        foreach (var name in routeArgs)
        {
            routeData.Values[name] = arguments[name];
        }

        var actionContext = new ActionContext(http, routeData, descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), arguments, controller: new object());
    }

    /// <summary>Runs the filter; returns the short-circuit status code, or null if the action ran.</summary>
    public async Task<int?> RunFilter(ClaimsPrincipal principal, IDictionary<string, object?> arguments, string method = "POST", string? template = null)
        => (await RunFilterWithBody(principal, arguments, method, template)).Status;

    /// <summary>Runs the filter; returns the short-circuit status and body text, or (null, null) if the action ran.</summary>
    public Task<(int? Status, string? Body)> RunFilterWithBody(ClaimsPrincipal principal, IDictionary<string, object?> arguments, string method = "POST", string? template = null)
        => RunAt(principal, arguments, null, null, method, template);

    /// <summary>Runs the filter as a specific controller action; <paramref name="routeArgs"/> are route-bound.</summary>
    /// <summary>Runs the filter as a controller action whose action returns <paramref name="result"/>; returns the final result.</summary>
    public async Task<IActionResult?> RunWithResult(ClaimsPrincipal principal, string controller, string action, IActionResult result, IDictionary<string, object?>? arguments = null)
    {
        var filter = new Filters.LiveTvAccessFilter(Access, Signals, Health, NullLogger<Filters.LiveTvAccessFilter>.Instance);
        var context = Context(principal, arguments ?? new Dictionary<string, object?>(), "GET", null, controller, action);
        ActionExecutedContext? executed = null;
        await filter.OnActionExecutionAsync(context, () =>
        {
            executed = new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller) { Result = result };
            return Task.FromResult(executed);
        });
        return executed?.Result ?? context.Result;
    }

    public async Task<(int? Status, string? Body)> RunAt(
        ClaimsPrincipal principal,
        IDictionary<string, object?> arguments,
        string? controller,
        string? action,
        string method = "POST",
        string? template = null,
        params string[] routeArgs)
    {
        var filter = new Filters.LiveTvAccessFilter(Access, Signals, Health, NullLogger<Filters.LiveTvAccessFilter>.Instance);
        var context = Context(principal, arguments, method, template, controller, action, routeArgs);
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, new List<IFilterMetadata>(), context.Controller));
        });
        return ran ? (null, null) : ((context.Result as ContentResult)?.StatusCode ?? -1, (context.Result as ContentResult)?.Content);
    }
}
