using System;
using System.Collections.Generic;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class UserTunerEntryTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid UserB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly string[] Keys = ["tuner1", "tuner2"];

    private static (UserTunerMap Map, List<string> Warnings) Build(params UserTunerEntry?[] entries)
    {
        var warnings = new List<string>();
        return (UserTunerMap.FromEntries(Keys, entries, warnings.Add), warnings);
    }

    [Fact]
    public void ValidEntries_AreMapped_WithoutWarnings()
    {
        var (map, warnings) = Build(new(UserA, "tuner1", "A"), new(UserB, "TUNER2", "B"));
        Assert.Equal("tuner1", map.GetTuner(UserA));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Empty(warnings);
    }

    [Fact]
    public void NullEntry_IsSkipped_OthersKeepAccess()
    {
        var (map, warnings) = Build(null, new(UserB, "tuner2", "B"));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Single(warnings);
    }

    [Fact]
    public void EntryWithoutUserId_IsSkipped_OthersKeepAccess()
    {
        var (map, warnings) = Build(new(Guid.Empty, "tuner1", "ghost"), new(UserB, "tuner2", "B"));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Contains(warnings, w => w.Contains("ghost", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tuner9")]
    [InlineData("bad key")]
    [InlineData(null)]
    public void EntryWithUnknownOrInvalidTuner_IsSkipped_OthersKeepAccess(string? key)
    {
        var (map, warnings) = Build(new(UserA, key, "A"), new(UserB, "tuner2", "B"));
        Assert.Null(map.GetTuner(UserA));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Single(warnings);
    }

    [Fact]
    public void DuplicateEntriesForSameTuner_AreMergedWithWarning()
    {
        var (map, warnings) = Build(new(UserA, "tuner1", "A"), new(UserA, "Tuner1", "a"), new(UserB, "tuner2", "B"));
        Assert.Equal("tuner1", map.GetTuner(UserA));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Single(warnings);
    }

    [Fact]
    public void ConflictingEntriesForOneUser_DenyThatUserOnly()
    {
        var (map, warnings) = Build(new(UserA, "tuner1", "A"), new(UserA, "tuner2", "a"), new(UserB, "tuner2", "B"));
        Assert.Null(map.GetTuner(UserA));
        Assert.Equal("tuner2", map.GetTuner(UserB));
        Assert.Single(warnings);
    }
}
