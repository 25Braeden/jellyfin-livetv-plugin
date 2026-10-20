using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>What the filter relies on for one action.</summary>
/// <param name="Controller">The controller name.</param>
/// <param name="Action">The action name.</param>
/// <param name="Parameters">Parameter names that must exist.</param>
/// <param name="BodyProperties">Properties that must exist on some parameter's type (body DTOs).</param>
/// <param name="Returns">A type the return type must contain (the scrubber recognizes it), if any.</param>
/// <param name="TemplatePrefix">A prefix the route template must have, if any.</param>
public sealed record EndpointRequirement(
    string Controller,
    string Action,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<string>? BodyProperties = null,
    Type? Returns = null,
    string? TemplatePrefix = null);
