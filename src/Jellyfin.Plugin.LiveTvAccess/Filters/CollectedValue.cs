using System;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// What an argument is known to be used for, beyond its shape.
/// </summary>
public enum ValueHint
{
    /// <summary>Nothing beyond its shape.</summary>
    None,

    /// <summary>A play session id (query <c>playSessionId</c>, legacy <c>params</c> field 20, body <c>PlaySessionId</c>):
    /// streaming reuses the transcoding job with this id (Jellyfin.Api/Helpers/StreamingHelpers.cs:117-125).</summary>
    PlaySessionId,
}

/// <summary>
/// One identifier-shaped value found in a request's arguments.
/// </summary>
/// <param name="Text">The value as text (GUID "N" format for GUIDs).</param>
/// <param name="Guid">The GUID, if the value is or parses as one.</param>
/// <param name="IsRouteIdentifier">Whether it is a route content identifier (must resolve).</param>
/// <param name="Hint">What the argument is used for, if known.</param>
/// <param name="Raw">The value exactly as sent.</param>
public sealed record CollectedValue(string Text, Guid? Guid, bool IsRouteIdentifier, ValueHint Hint = ValueHint.None, string? Raw = null);
