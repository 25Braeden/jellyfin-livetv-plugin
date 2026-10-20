using System;
using System.Collections.Generic;
using System.Net;

namespace LiveTvAccess.Core;

/// <summary>
/// Rules for the server-internal live buffer URLs (<c>LiveTv/LiveStreamFiles/{id}/stream.ts</c>,
/// <c>LiveTv/LiveRecordings/{id}/stream</c>). Jellyfin's own ffmpeg reads them anonymously, through
/// <c>GetApiUrlForLocalAccess()</c>: loopback or the server's own interface address
/// (src/Jellyfin.LiveTv/TunerHosts/SharedHttpStream.cs:66, Emby.Server.Implementations/ApplicationHost.cs:919-926,
/// src/Jellyfin.Networking/Manager/NetworkManager.cs:866-870). Nobody else needs them.
/// </summary>
public static class BufferAccess
{
    /// <summary>
    /// Gets the headers that mark a request as coming through a proxy. <c>X-Original-*</c> are what ASP.NET's forwarded
    /// headers middleware leaves behind after consuming <c>X-Forwarded-*</c> from a known proxy.
    /// </summary>
    public static IReadOnlySet<string> ForwardedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Forwarded-Prefix",
        "X-Forwarded-Server", "X-Real-IP", "Forwarded", "X-Original-For", "X-Original-Host", "X-Original-Proto",
        "X-Original-Prefix", "X-Client-IP", "True-Client-IP", "CF-Connecting-IP",
    };

    /// <summary>
    /// Gets whether any of the request's header names marks it as forwarded by a proxy.
    /// </summary>
    /// <param name="headerNames">The request's header names.</param>
    /// <returns><c>true</c> if forwarded.</returns>
    public static bool HasForwardedHeaders(IEnumerable<string> headerNames)
    {
        ArgumentNullException.ThrowIfNull(headerNames);
        foreach (var name in headerNames)
        {
            if (ForwardedHeaders.Contains(name))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets whether a connection comes from the server itself: loopback, the address it connected to (a connection to
    /// the server's own interface address comes from that same address), or one of the server's addresses.
    /// </summary>
    /// <param name="remote">The connection's remote address.</param>
    /// <param name="local">The connection's local address.</param>
    /// <param name="serverAddresses">The server's own addresses, consulted only if needed.</param>
    /// <returns><c>true</c> if the server is the caller.</returns>
    public static bool IsServerLocal(IPAddress? remote, IPAddress? local, Func<IEnumerable<IPAddress>> serverAddresses)
    {
        ArgumentNullException.ThrowIfNull(serverAddresses);
        if (remote is null)
        {
            return false;
        }

        remote = Normalize(remote);
        if (IPAddress.IsLoopback(remote))
        {
            return true;
        }

        if (local is not null && remote.Equals(Normalize(local)))
        {
            return true;
        }

        foreach (var address in serverAddresses())
        {
            if (remote.Equals(Normalize(address)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides a buffer URL request. Admins and API keys keep access; anyone else only from the server itself and with
    /// no forwarded headers (anything through the reverse proxy carries them).
    /// </summary>
    /// <param name="caller">The caller (anonymous: no user, no access).</param>
    /// <param name="isServerLocal">Whether the connection comes from the server itself.</param>
    /// <param name="isForwarded">Whether the request carries forwarded headers.</param>
    /// <returns>The decision.</returns>
    public static AccessDecision Decide(Caller caller, bool isServerLocal, bool isForwarded)
        => AccessPolicy.IsExempt(caller) || (isServerLocal && !isForwarded)
            ? AccessDecision.Allow
            : AccessDecision.DenyBufferNotLocal;

    private static IPAddress Normalize(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
