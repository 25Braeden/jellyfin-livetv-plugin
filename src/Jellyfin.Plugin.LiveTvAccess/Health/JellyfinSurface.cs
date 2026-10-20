using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.LiveTvAccess.Filters;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.LiveTvAccess.Health;

/// <summary>
/// Reads the running server: MVC options, the action descriptors, and Jellyfin's claim type constants
/// (Jellyfin.Api/Constants/InternalClaimTypes.cs, read by reflection; the plugin doesn't reference Jellyfin.Api).
/// </summary>
public sealed class JellyfinSurface : IJellyfinSurface
{
    private const string ClaimTypesTypeName = "Jellyfin.Api.Constants.InternalClaimTypes";

    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyfinSurface"/> class.
    /// </summary>
    /// <param name="services">The service provider (MVC services are resolved when checked, not at startup).</param>
    public JellyfinSurface(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public bool IsFilterRegistered()
    {
        var filters = _services.GetService<IOptions<MvcOptions>>()?.Value.Filters;
        return filters is not null && filters.Any(f =>
            (f is TypeFilterAttribute t && t.ImplementationType == typeof(LiveTvAccessFilter))
            || (f is ServiceFilterAttribute s && s.ServiceType == typeof(LiveTvAccessFilter)));
    }

    /// <inheritdoc />
    public IReadOnlyList<ActionShape> GetActions()
    {
        var provider = _services.GetService<IActionDescriptorCollectionProvider>();
        if (provider is null)
        {
            return [];
        }

        return provider.ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Select(a => new ActionShape(
                a.ControllerName,
                a.ActionName,
                a.Parameters.Select(p => new ParameterShape(p.Name, p.ParameterType)).ToArray(),
                a.MethodInfo.ReturnType,
                a.AttributeRouteInfo?.Template))
            .ToArray();
    }

    /// <inheritdoc />
    public (string UserId, string IsApiKey)? GetClaimTypes()
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(ClaimTypesTypeName, throwOnError: false))
            .FirstOrDefault(t => t is not null);
        if (type is null)
        {
            return null;
        }

        static string? Constant(Type type, string name)
            => type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue() as string;

        var userId = Constant(type, "UserId");
        var isApiKey = Constant(type, "IsApiKey");
        return userId is null || isApiKey is null ? null : (userId, isApiKey);
    }
}
