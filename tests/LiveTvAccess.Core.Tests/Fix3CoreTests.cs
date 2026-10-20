using System;
using System.Collections.Generic;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix3CoreTests
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-0000-0000-00000000000a");

    [Theory]
    [InlineData("unassigned")]
    [InlineData("UNASSIGNED")]
    [InlineData(" Unassigned ")]
    public void UnassignedIsAReservedTunerKey(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => TunerTags.NormalizeKey(key));
    }

    [Fact]
    public void Owned_IsConfiguredTunersPlusUnassignedPlusHistory()
    {
        var owned = TagOwnership.Owned(["tuner1", "tuner2"], ["livetv-tuner9", "not-ours", "LIVETV-Old"]);
        Assert.Equal(
            new HashSet<string>(["livetv-tuner1", "livetv-tuner2", "livetv-unassigned", "livetv-tuner9", "livetv-old"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("LIVETV-TUNER1", owned);
        Assert.DoesNotContain("not-ours", owned);
    }

    [Fact]
    public void Merge_ReplacesOwnedTags_KeepsEverythingElse_ReportsForeignLivetvTags()
    {
        var owned = TagOwnership.Owned(["tuner1", "tuner2"], ["livetv-tuner9"]);
        var merged = TagOwnership.Merge(
            ["kids", "livetv-tuner1", "livetv-sports", "livetv-tuner9"], owned, ["livetv-tuner2", "livetv-unassigned"], out var foreign);

        Assert.Equal(["kids", "livetv-sports", "livetv-tuner2", "livetv-unassigned"], merged);
        Assert.Equal(["livetv-sports"], foreign);
    }

    [Fact]
    public void Merge_IsCaseInsensitive_AndDoesNotDuplicate()
    {
        var owned = TagOwnership.Owned(["tuner1"], []);
        var merged = TagOwnership.Merge(["LIVETV-TUNER1", "Kids"], owned, ["livetv-tuner1"], out var foreign);
        Assert.Equal(["Kids", "livetv-tuner1"], merged);
        Assert.Empty(foreign);
    }

    [Fact]
    public void TunerCatalog_FromEntries_SkipsBadEntriesOnly()
    {
        var warnings = new List<string>();
        var catalog = TunerCatalog.FromEntries(
            [
                new TunerEntry("tuner1", "/t1.m3u"),
                new TunerEntry("unassigned", "/t2.m3u"),
                new TunerEntry("bad key", "/t3.m3u"),
                new TunerEntry("tuner4", null),
                null,
                new TunerEntry("dup", "/d1.m3u"),
                new TunerEntry("DUP", "/d2.m3u"),
                new TunerEntry("x", "/t1.m3u"),
                new TunerEntry("tuner5", "/t5.m3u"),
            ],
            warnings.Add);

        Assert.Equal(["tuner5"], catalog.Keys);
        Assert.Equal(8, warnings.Count); // one per skipped entry, incl. both sides of each duplicate
    }

    [Fact]
    public void TunerCatalog_FromEntries_ValidEntries_NoWarnings()
    {
        var warnings = new List<string>();
        var catalog = TunerCatalog.FromEntries([new TunerEntry("tuner1", "/t1.m3u"), new TunerEntry("tuner2", "/t2.m3u")], warnings.Add);
        Assert.Equal(["tuner1", "tuner2"], catalog.Keys);
        Assert.Empty(warnings);
    }

    [Fact]
    public void IsExempt_AdminsAndUserlessApiKeysOnly()
    {
        Assert.True(AccessPolicy.IsExempt(new Caller(UserA, false, true, true)));
        Assert.True(AccessPolicy.IsExempt(new Caller(Guid.Empty, true, false, true)));
        Assert.False(AccessPolicy.IsExempt(new Caller(UserA, true, false, true)));
        Assert.False(AccessPolicy.IsExempt(new Caller(UserA, false, false, true)));
        Assert.False(AccessPolicy.IsExempt(new Caller(Guid.Empty, false, false, true)));
    }
}
