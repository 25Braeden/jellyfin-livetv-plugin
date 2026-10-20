using System;
using System.Collections.Generic;
using System.Net;
using LiveTvAccess.Core;
using Xunit;

namespace LiveTvAccess.Core.Tests;

public class Fix6CoreTests
{
    private static readonly Caller User = new(Guid.Parse("00000000-0000-0000-0000-00000000000a"), false, false, true);
    private static readonly Caller Admin = new(Guid.Parse("00000000-0000-0000-0000-00000000000b"), false, true, true);
    private static readonly Caller ApiKey = new(Guid.Empty, true, false, false);
    private static readonly Caller Anonymous = new(Guid.Empty, false, false, false);

    private static IEnumerable<IPAddress> Server() => [IPAddress.Parse("172.18.0.5"), IPAddress.Parse("fd00::5")];

    [Theory]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("127.0.0.42", null, true)]
    [InlineData("::1", null, true)]
    [InlineData("::ffff:127.0.0.1", null, true)]
    [InlineData("172.18.0.9", "172.18.0.9", true)]
    [InlineData("::ffff:172.18.0.9", "172.18.0.9", true)]
    [InlineData("172.18.0.5", "10.0.0.1", true)]
    [InlineData("fd00::5", null, true)]
    [InlineData("172.18.0.9", "172.18.0.5", false)]
    [InlineData("192.168.65.1", "172.18.0.5", false)]
    [InlineData("8.8.8.8", null, false)]
    public void IsServerLocal(string remote, string? local, bool expected)
    {
        Assert.Equal(expected, BufferAccess.IsServerLocal(IPAddress.Parse(remote), local is null ? null : IPAddress.Parse(local), Server));
    }

    [Fact]
    public void IsServerLocal_NoRemoteAddress_IsNotLocal()
    {
        Assert.False(BufferAccess.IsServerLocal(null, IPAddress.Loopback, Server));
    }

    [Theory]
    [InlineData("X-Forwarded-For")]
    [InlineData("x-forwarded-for")]
    [InlineData("X-Real-IP")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-Original-For")]
    [InlineData("X-Original-Proto")]
    [InlineData("X-Original-Host")]
    public void ForwardedHeaders_AreDetected(string header)
    {
        Assert.True(BufferAccess.HasForwardedHeaders(["Host", "User-Agent", header]));
    }

    [Fact]
    public void PlainRequest_IsNotForwarded()
    {
        Assert.False(BufferAccess.HasForwardedHeaders(["Host", "User-Agent", "Accept", "Icy-MetaData", "Range"]));
    }

    [Fact]
    public void Decide()
    {
        Assert.Equal(AccessDecision.Allow, BufferAccess.Decide(Anonymous, true, false));
        Assert.Equal(AccessDecision.DenyBufferNotLocal, BufferAccess.Decide(Anonymous, true, true));
        Assert.Equal(AccessDecision.DenyBufferNotLocal, BufferAccess.Decide(Anonymous, false, false));
        Assert.Equal(AccessDecision.DenyBufferNotLocal, BufferAccess.Decide(User, false, false));
        Assert.Equal(AccessDecision.Allow, BufferAccess.Decide(Admin, false, true));
        Assert.Equal(AccessDecision.Allow, BufferAccess.Decide(ApiKey, false, true));
        Assert.Equal(AccessPolicy.DeniedMessage, AccessPolicy.Message(AccessDecision.DenyBufferNotLocal));
    }
}
