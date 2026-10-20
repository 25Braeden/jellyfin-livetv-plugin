using System;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix5eCoreTests
{
    private static readonly Caller User = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"), false, false, true);
    private static readonly Caller Admin = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"), false, true, true);

    [Theory]
    [InlineData(true, true, false, AccessDecision.DenyUntrackedStream)]
    [InlineData(true, true, true, AccessDecision.Allow)]
    [InlineData(true, false, false, AccessDecision.Allow)]
    [InlineData(false, true, false, AccessDecision.Allow)]
    public void DecideStatic(bool isStatic, bool namesLiveTv, bool tracked, AccessDecision expected)
    {
        Assert.Equal(expected, AccessPolicy.DecideStatic(User, isStatic, namesLiveTv, tracked));
    }

    [Fact]
    public void DecideStatic_ExemptCallersPass()
    {
        Assert.Equal(AccessDecision.Allow, AccessPolicy.DecideStatic(Admin, true, true, false));
    }

    [Fact]
    public void UntrackedStream_UsesTheGenericMessage()
    {
        Assert.Equal(AccessPolicy.DeniedMessage, AccessPolicy.Message(AccessDecision.DenyUntrackedStream));
    }
}
