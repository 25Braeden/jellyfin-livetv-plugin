namespace LiveTvAccess.Core;

/// <summary>
/// How Jellyfin 10.11.11 decides, for a Videos/Audio stream request, whether it is static and which open live stream it
/// uses. Only these effective parameters count; no other argument can make a static request "tracked". Version-fragile.
/// </summary>
public static class StaticStreamRequest
{
    /// <summary>Legacy <c>params</c> field that sets <c>Static</c> (Jellyfin.Api/Helpers/StreamingHelpers.cs:417-418).</summary>
    public const int ParamsStaticField = 3;

    /// <summary>Legacy <c>params</c> field that sets <c>LiveStreamId</c> (StreamingHelpers.cs:525-526).</summary>
    public const int ParamsLiveStreamIdField = 22;

    /// <summary>
    /// Gets whether the request is static. A non-blank <c>params</c> field overrides the bound argument, as in
    /// Jellyfin's ParseParams (StreamingHelpers.cs:62, :389-418). Jellyfin treats only "true" as static; this fails
    /// closed: any non-blank field value other than "false" counts as static.
    /// </summary>
    /// <param name="staticArgument">The bound <c>static</c> argument.</param>
    /// <param name="legacyParams">The legacy <c>params</c> string.</param>
    /// <returns><c>true</c> if (possibly) static.</returns>
    public static bool IsStatic(bool? staticArgument, string? legacyParams)
    {
        var field = Field(legacyParams, ParamsStaticField);
        return field is not null
            ? !string.Equals(field, "false", System.StringComparison.OrdinalIgnoreCase)
            : staticArgument == true;
    }

    /// <summary>
    /// Gets the live stream id the request uses: a non-blank <c>params</c> field 22 overrides the bound
    /// <c>liveStreamId</c>; a blank value means none (StreamingHelpers.cs:116, :525-526).
    /// </summary>
    /// <param name="liveStreamIdArgument">The bound <c>liveStreamId</c> argument.</param>
    /// <param name="legacyParams">The legacy <c>params</c> string.</param>
    /// <returns>The effective live stream id, or <c>null</c>.</returns>
    public static string? EffectiveLiveStreamId(string? liveStreamIdArgument, string? legacyParams)
        => Field(legacyParams, ParamsLiveStreamIdField)
            ?? (string.IsNullOrWhiteSpace(liveStreamIdArgument) ? null : liveStreamIdArgument);

    /// <summary>A non-blank <c>params</c> field, as Jellyfin reads it (split on ';', blank fields skipped).</summary>
    private static string? Field(string? legacyParams, int index)
    {
        if (string.IsNullOrEmpty(legacyParams))
        {
            return null;
        }

        var fields = legacyParams.Split(';');
        return fields.Length > index && !string.IsNullOrWhiteSpace(fields[index]) ? fields[index] : null;
    }
}
