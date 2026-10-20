using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>One controller action as the self-check sees it.</summary>
/// <param name="Controller">The controller name.</param>
/// <param name="Action">The action name.</param>
/// <param name="Parameters">The parameters.</param>
/// <param name="ReturnType">The action method's return type.</param>
/// <param name="Template">The attribute route template.</param>
public sealed record ActionShape(string Controller, string Action, IReadOnlyList<ParameterShape> Parameters, Type? ReturnType, string? Template);
