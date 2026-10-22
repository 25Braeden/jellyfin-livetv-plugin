using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Port to Jellyfin 12.2: <c>SessionInfoDto.NowPlayingQueueFullItems</c> was removed upstream
/// (MediaBrowser.Model/Dto/SessionInfoDto.cs in 10.11.11 :155 vs 12.2, where it is gone and
/// <c>NowPlayingQueue</c> is still id-only <c>QueueItem</c>s). The 12.2 session DTO carries full
/// <c>BaseItemDto</c>s in exactly two places: <c>NowPlayingItem</c> and <c>NowViewingItem</c>
/// (12.2 Emby.Server.Implementations/Session/SessionManager.cs:1271-1272, populated by
/// <c>GetItemInfo</c> :1924 and :2004). Both must be scrubbed for non-admins.
/// </summary>
public class Port12Tests
{
    private const string Upstream = "http://provider.example/live/user/secret/123.ts";

    [Fact]
    public async Task Sessions_NowPlayingAndNowViewingLiveSources_AreScrubbed()
    {
        var w = new World();
        BaseItemDto LiveTvDto() => new()
        {
            Type = Jellyfin.Data.Enums.BaseItemKind.TvChannel,
            MediaSources = [new MediaSourceInfo { Path = Upstream }],
        };
        var session = new SessionInfoDto
        {
            NowPlayingItem = LiveTvDto(),
            NowViewingItem = LiveTvDto(),
            NowPlayingQueue = [new QueueItem { Id = w.Tuner1Channel.Id, PlaylistItemId = "pl-1" }],
        };
        var result = await w.RunWithResult(World.Principal(w.UserA.Id), "Session", "GetSessions", new ObjectResult(new[] { session }));
        var scrubbed = ((IEnumerable<SessionInfoDto>)((ObjectResult)result!).Value!).Single();
        Assert.True(string.IsNullOrEmpty(scrubbed.NowPlayingItem!.MediaSources![0].Path));
        Assert.True(string.IsNullOrEmpty(scrubbed.NowViewingItem!.MediaSources![0].Path));
        Assert.Equal(w.Tuner1Channel.Id, scrubbed.NowPlayingQueue![0].Id);

        // The server's own object is untouched.
        Assert.Equal(Upstream, session.NowPlayingItem.MediaSources[0].Path);
        Assert.Equal(Upstream, session.NowViewingItem.MediaSources[0].Path);
    }
}
