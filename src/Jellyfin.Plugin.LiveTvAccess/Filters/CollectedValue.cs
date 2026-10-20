using System;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// One identifier-shaped value found in a request's action arguments.
/// </summary>
/// <param name="Text">The value as text (GUID "N" format for GUIDs).</param>
/// <param name="Guid">The GUID, if the value is or parses as one.</param>
/// <param name="IsRouteIdentifier">Whether it is a route content identifier (must resolve).</param>
public sealed record CollectedValue(string Text, Guid? Guid, bool IsRouteIdentifier);
