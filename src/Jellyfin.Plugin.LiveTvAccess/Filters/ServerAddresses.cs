using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// The server's own unicast addresses, cached for a minute. Only consulted for a buffer request that isn't from
/// loopback or the address it connected to.
/// </summary>
public static class ServerAddresses
{
    private static readonly TimeSpan _maxAge = TimeSpan.FromMinutes(1);
    private static (DateTime At, IPAddress[] Addresses) _cache = (DateTime.MinValue, []);

    /// <summary>
    /// Gets the server's own addresses. On failure, none (fail closed: only loopback and the connected address count).
    /// </summary>
    /// <returns>The addresses.</returns>
    public static IEnumerable<IPAddress> Get()
    {
        var cache = _cache;
        if (DateTime.UtcNow - cache.At < _maxAge)
        {
            return cache.Addresses;
        }

        IPAddress[] addresses;
        try
        {
            addresses = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .ToArray();
        }
#pragma warning disable CA1031 // Can't list interfaces: no extra addresses.
        catch (Exception)
#pragma warning restore CA1031
        {
            addresses = [];
        }

        _cache = (DateTime.UtcNow, addresses);
        return addresses;
    }
}
