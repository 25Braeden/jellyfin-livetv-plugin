using System;
using System.Collections.Generic;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class CoreTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid UserC = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    private static UserTunerMap Map() => new(
        ["tuner1", "Tuner2"],
        [new KeyValuePair<Guid, string>(UserA, "tuner1"), new KeyValuePair<Guid, string>(UserB, "TUNER2")]);

    [Fact]
    public void ForTuner_NormalizesToLowercaseWithPrefix()
    {
        Assert.Equal("livetv-tuner1", TunerTags.ForTuner(" Tuner1 "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tuner 1")]
    [InlineData("tuner_1")]
    public void NormalizeKey_RejectsInvalidKeys(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => TunerTags.NormalizeKey(key));
    }

    [Theory]
    [InlineData("livetv-tuner1", true)]
    [InlineData("LiveTV-x", true)]
    [InlineData("movies", false)]
    [InlineData(null, false)]
    public void IsManaged_MatchesPrefixOnly(string? tag, bool expected)
    {
        Assert.Equal(expected, TunerTags.IsManaged(tag));
    }

    [Fact]
    public void GetBlockedTags_BlocksEveryOtherTuner()
    {
        var map = Map();
        Assert.Equal(["livetv-tuner2"], map.GetBlockedTags(UserA));
        Assert.Equal(["livetv-tuner1"], map.GetBlockedTags(UserB));
    }

    [Fact]
    public void GetBlockedTags_UnassignedUserIsDeniedEverything()
    {
        Assert.Null(Map().GetTuner(UserC));
        Assert.Equal(["livetv-tuner1", "livetv-tuner2"], Map().GetBlockedTags(UserC));
    }

    [Fact]
    public void Constructor_RejectsUnknownTunerAndDuplicateUser()
    {
        Assert.Throws<ArgumentException>(() => new UserTunerMap(
            ["tuner1"], [new KeyValuePair<Guid, string>(UserA, "tuner9")]));
        Assert.Throws<ArgumentException>(() => new UserTunerMap(
            ["tuner1"],
            [new KeyValuePair<Guid, string>(UserA, "tuner1"), new KeyValuePair<Guid, string>(UserA, "tuner1")]));
    }
}
