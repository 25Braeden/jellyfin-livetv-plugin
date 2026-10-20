using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using LiveTvAccess.Core;
using Xunit;

namespace Jellyfin.Plugin.LiveTvAccess.Tests;

/// <summary>
/// Final review Q2: the filter resolves at most <see cref="TargetCollector.MaxDistinctIdentifiers"/> distinct
/// identifiers per request; above that it refuses before doing any lookup. Repeats are resolved once.
/// </summary>
public class ReviewFinalCapTests
{
    private static object Body(IEnumerable<string> values) => new { Profiles = values.Select(v => new { Container = v }).ToList() };

    private static (Task<(int? Status, string? Body)> Run, Func<int> Lookups) Post(World w, Guid caller, object body)
    {
        var lookups = 0;
        w.OnGetItemById = _ => lookups++;
        var run = w.RunAt(World.Principal(caller), new Dictionary<string, object?> { ["body"] = body }, "MediaInfo", "GetPostedPlaybackInfo");
        return (run, () => lookups);
    }

    [Fact]
    public void TheCapIsANamedConstant_OfOneThousand()
    {
        Assert.Equal(1000, TargetCollector.MaxDistinctIdentifiers);
    }

    [Fact]
    public async Task MoreDistinctIdentifiersThanTheCap_IsRefused_BeforeAnyLookup()
    {
        var w = new World();
        var (run, lookups) = Post(w, w.UserA.Id, Body(Enumerable.Range(0, TargetCollector.MaxDistinctIdentifiers + 1).Select(_ => Guid.NewGuid().ToString("N"))));
        var watch = Stopwatch.StartNew();
        var r = await run;
        Assert.Equal(403, r.Status);
        Assert.Equal(AccessPolicy.DeniedMessage, r.Body);
        Assert.Equal(0, lookups());
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ExactlyTheCap_IsResolved()
    {
        var w = new World();
        var (run, lookups) = Post(w, w.UserA.Id, Body(Enumerable.Range(0, TargetCollector.MaxDistinctIdentifiers).Select(_ => Guid.NewGuid().ToString("N"))));
        Assert.Null((await run).Status);
        Assert.Equal(TargetCollector.MaxDistinctIdentifiers, lookups());
    }

    [Fact]
    public async Task RepeatedIdentifiers_CountOnce_AndResolveOnce()
    {
        var w = new World();
        var id = w.Movie.Id.ToString("N");
        var (run, lookups) = Post(w, w.UserA.Id, Body(Enumerable.Repeat(id, 5000)));
        Assert.Null((await run).Status);
        Assert.Equal(1, lookups());
    }

    [Fact]
    public async Task ARepeatedOtherTunerChannel_IsStillDenied()
    {
        var w = new World();
        var values = Enumerable.Repeat(w.Movie.Id.ToString("N"), 10).Append(w.Tuner2Channel.Id.ToString("N")).Append(w.Tuner2Channel.Id.ToString("N"));
        var (run, _) = Post(w, w.UserA.Id, Body(values));
        Assert.Equal(403, (await run).Status);
    }

    [Fact]
    public async Task Admins_AreNotCapped()
    {
        var w = new World();
        var (run, _) = Post(w, w.Admin.Id, Body(Enumerable.Range(0, 3000).Select(_ => Guid.NewGuid().ToString("N"))));
        Assert.Null((await run).Status);
    }
}
