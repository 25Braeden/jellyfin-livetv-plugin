using System;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>One action parameter as the self-check sees it.</summary>
/// <param name="Name">The parameter name (as bound: <c>@static</c> is <c>static</c>).</param>
/// <param name="Type">The parameter type.</param>
public sealed record ParameterShape(string Name, Type Type);
