using System;
using System.Collections.Generic;
using System.Linq;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix4CoreTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    private static UserTunerMap Map() => new(["tuner1"], [new KeyValuePair<Guid, string>(UserA, "tuner1")]);

    [Fact]
    public void MappedUser_WithoutNativeLiveTvPermission_IsDenied()
    {
        Assert.Equal(AccessDecision.DenyNoLiveTvAccess, AccessPolicy.Decide(new Caller(UserA, false, false, false), Map(), "tuner1"));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(UserA, false, false, true), Map(), "tuner1"));
    }

    [Fact]
    public void UnidentifiedCaller_IsDenied()
    {
        Assert.Equal(AccessDecision.DenyNoLiveTvAccess, AccessPolicy.Decide(new Caller(Guid.Empty, false, false, false), Map(), "tuner1"));
    }

    [Fact]
    public void ExemptCallers_DoNotNeedTheNativeFlag()
    {
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(UserA, false, true, false), Map(), "tuner1"));
        Assert.Equal(AccessDecision.Allow, AccessPolicy.Decide(new Caller(Guid.Empty, true, false, false), Map(), null));
    }

    [Fact]
    public void EveryDenialReason_ReturnsTheSameGenericMessage()
    {
        var messages = Enum.GetValues<AccessDecision>()
            .Where(d => d != AccessDecision.Allow)
            .Select(AccessPolicy.Message)
            .Distinct()
            .ToList();
        Assert.Single(messages);
        Assert.False(string.IsNullOrWhiteSpace(messages[0]));
    }
}
