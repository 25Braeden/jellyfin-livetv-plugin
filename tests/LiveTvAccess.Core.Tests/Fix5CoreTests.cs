using System;
using System.Collections.Generic;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix5CoreTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserC = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid Channel = Guid.Parse("fe006602fc01342e1f5d63b0b0f9f40a");
    private const string P = "e2329f4997b378e64ccf8fa396deb76e";

    private static UserTunerMap Map() => new(["tuner1", "tuner2"], [new KeyValuePair<Guid, string>(UserA, "tuner1")]);

    private static Caller A => new(UserA, false, false, true);

    [Theory]
    [InlineData(P + "_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a_4ddd999f02971aec37a820d93a68cb59", ProviderStringKind.LiveTvOpenToken, true)]
    [InlineData(P + "_LiveTvChannel_fe006602-fc01-342e-1f5d-63b0b0f9f40a", ProviderStringKind.LiveTvOpenToken, true)]
    [InlineData(P + "_LiveTvChannel_not-a-guid", ProviderStringKind.LiveTvOpenToken, false)]
    [InlineData(P + "_af999c25a00715699361240d4c6c7a53_4ddd999f02971aec37a820d93a68cb59", ProviderStringKind.LiveTvStreamId, false)]
    [InlineData("E2329F4997B378E64CCF8FA396DEB76E_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a", ProviderStringKind.LiveTvOpenToken, true)]
    [InlineData("0123456789abcdef0123456789abcdef_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a", ProviderStringKind.OtherProvider, false)]
    [InlineData("0123456789abcdef0123456789abcdef_x", ProviderStringKind.OtherProvider, false)]
    [InlineData("fe006602fc01342e1f5d63b0b0f9f40a", ProviderStringKind.None, false)]
    [InlineData("hello_world", ProviderStringKind.None, false)]
    [InlineData(" " + P + "_LiveTvChannel_fe006602fc01342e1f5d63b0b0f9f40a", ProviderStringKind.OtherProvider, false)]
    [InlineData(null, ProviderStringKind.None, false)]
    public void ProviderString_Classify(string? value, ProviderStringKind expected, bool hasItem)
    {
        Assert.Equal(expected, ProviderString.Classify(value, out var id));
        Assert.Equal(hasItem ? Channel : Guid.Empty, id);
    }

    [Theory]
    [InlineData("fe006602fc01342e1f5d63b0b0f9f40a", true)]
    [InlineData("fe006602-fc01-342e-1f5d-63b0b0f9f40a", true)]
    [InlineData("{fe006602-fc01-342e-1f5d-63b0b0f9f40a}", true)]
    [InlineData("main", false)]
    [InlineData("12345", false)]
    [InlineData(null, false)]
    public void TryParseGuid_AcceptsTheFormatsJellyfinAccepts(string? value, bool expected)
    {
        Assert.Equal(expected, ProviderString.TryParseGuid(value, out _));
    }

    [Fact]
    public void DecideTarget_Rules()
    {
        var map = Map();
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideTarget(A, map, new(TargetKind.NotLiveTv, null, true), true));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideTarget(A, map, new(TargetKind.LiveTv, "tuner1", true), true));
        Assert.Equal(AccessDecision.DenyOtherTuner, AccessPolicy.DecideTarget(A, map, new(TargetKind.LiveTv, "tuner2", false), true));
        Assert.Equal(AccessDecision.DenyRecording, AccessPolicy.DecideTarget(A, map, new(TargetKind.Recording, null, false), true));
        Assert.Equal(AccessDecision.DenyUnresolved, AccessPolicy.DecideTarget(A, map, new(TargetKind.Unresolved, null, true), true));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideTarget(A, map, new(TargetKind.Unresolved, null, false), true));
        Assert.Equal(AccessDecision.DenyNotReady, AccessPolicy.DecideTarget(A, map, new(TargetKind.LiveTv, "tuner1", true), false));
    }

    [Fact]
    public void DecideTarget_ExemptCallersPassEverything()
    {
        var admin = new Caller(UserC, false, true, false);
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideTarget(admin, Map(), new(TargetKind.Recording, null, true), false));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideTarget(admin, Map(), new(TargetKind.Unresolved, null, true), false));
    }

    [Fact]
    public void DecideEndpoint_Rules()
    {
        var map = Map();
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideEndpoint(A, map, true, true));
        Assert.Equal(AccessDecision.DenyEndpoint, AccessPolicy.DecideEndpoint(A, map, false, true));
        Assert.Equal(AccessDecision.DenyNoTuner, AccessPolicy.DecideEndpoint(new Caller(UserC, false, false, true), map, true, true));
        Assert.Equal(AccessDecision.DenyNoLiveTvAccess, AccessPolicy.DecideEndpoint(new Caller(UserA, false, false, false), map, true, true));
        Assert.Equal(AccessDecision.DenyNotReady, AccessPolicy.DecideEndpoint(A, map, true, false));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideEndpoint(new Caller(UserC, false, true, false), map, false, false));
    }
}
