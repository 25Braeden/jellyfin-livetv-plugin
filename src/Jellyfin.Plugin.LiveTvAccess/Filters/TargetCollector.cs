using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using LiveTvAccess.Core;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.LiveTvAccess.Filters;

/// <summary>
/// Walks every action argument (and every nested property of body objects of any type) and returns each
/// identifier-shaped value: GUIDs, GUID-shaped strings, and provider-prefixed tokens / stream ids. Not a list of
/// known parameter names: whatever an endpoint calls its arguments, identifiers in them are found.
/// </summary>
public static class TargetCollector
{
    /// <summary>Maximum values visited per request; beyond this the request is denied (fail closed).</summary>
    public const int MaxNodes = 50_000;

    /// <summary>Maximum nesting depth followed into body objects; deeper is denied (fail closed).</summary>
    public const int MaxDepth = 8;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> _properties = new();

    /// <summary>
    /// Collects identifier-shaped values from a request.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="values">The collected values.</param>
    /// <returns><c>false</c> if the request was too large or deep to inspect completely.</returns>
    public static bool TryCollect(ActionExecutingContext context, out IReadOnlyList<CollectedValue> values)
    {
        ArgumentNullException.ThrowIfNull(context);

        var walker = new Walker();
        foreach (var (name, value) in context.ActionArguments)
        {
            var isRouteIdentifier = LiveTvEndpoints.RouteIdentifiers.Contains(name) && context.RouteData.Values.ContainsKey(name);
            walker.Walk(value, isRouteIdentifier, 0, name);
        }

        values = walker.Values;
        return walker.Complete;
    }

    private static PropertyInfo[] Properties(Type type) => _properties.GetOrAdd(type, t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.GetMethod is { IsPublic: true })
        .ToArray());

    private static bool IsFrameworkType(Type type)
        => type.Namespace is { } ns && (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal));

    private sealed class Walker
    {
        private readonly HashSet<object> _visited = new(ReferenceEqualityComparer.Instance);
        private int _budget = MaxNodes;

        public List<CollectedValue> Values { get; } = new();

        public bool Complete { get; private set; } = true;

        public void Walk(object? value, bool isRouteIdentifier, int depth, string? name = null)
        {
            if (!Complete || value is null)
            {
                return;
            }

            if (--_budget < 0)
            {
                Complete = false;
                return;
            }

            // Arguments / properties with a known meaning, whatever their shape.
            if (value is string named && name is not null)
            {
                if (string.Equals(name, "params", StringComparison.OrdinalIgnoreCase))
                {
                    WalkParams(named);
                    return;
                }

                if (string.Equals(name, "playSessionId", StringComparison.OrdinalIgnoreCase))
                {
                    AddPlaySession(named);
                    return;
                }
            }

            switch (value)
            {
                case Guid guid:
                    Values.Add(new CollectedValue(guid.ToString("N"), guid, isRouteIdentifier));
                    return;
                case string text:
                    AddString(text, isRouteIdentifier);
                    return;
                case JsonElement json:
                    WalkJson(json, depth);
                    return;
                case IDictionary dictionary:
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        Walk(entry.Key, false, depth);
                        Walk(entry.Value, false, depth);
                    }

                    return;
                case IEnumerable sequence:
                    foreach (var item in sequence)
                    {
                        Walk(item, isRouteIdentifier, depth);
                        if (!Complete)
                        {
                            return;
                        }
                    }

                    return;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || IsFrameworkType(type))
            {
                if (isRouteIdentifier)
                {
                    // A route content identifier that isn't a GUID or string can't be resolved.
                    Values.Add(new CollectedValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, null, true));
                }

                return;
            }

            // A body object of any (non-framework) type: inspect every readable property.
            if (depth >= MaxDepth)
            {
                Complete = false;
                return;
            }

            if (!type.IsValueType && !_visited.Add(value))
            {
                return;
            }

            foreach (var property in Properties(type))
            {
                object? child;
                try
                {
                    child = property.GetValue(value);
                }
#pragma warning disable CA1031 // A throwing getter can't hide an identifier we never read: fail closed.
                catch (Exception)
#pragma warning restore CA1031
                {
                    Complete = false;
                    return;
                }

                Walk(child, false, depth + 1, property.Name);
            }
        }

        /// <summary>
        /// The legacy ";"-separated "params" string of the streaming / HLS endpoints, split exactly like
        /// Jellyfin.Api/Helpers/StreamingHelpers.cs:389-560 (field 2 MediaSourceId, 20 PlaySessionId, 22 LiveStreamId).
        /// Every field is inspected; field 20 is also looked up as a play session.
        /// </summary>
        private void WalkParams(string value)
        {
            string[] fields;
            try
            {
                fields = value.Split(';');
            }
#pragma warning disable CA1031 // Can't parse it: fail closed.
            catch (Exception)
#pragma warning restore CA1031
            {
                Complete = false;
                return;
            }

            for (var i = 0; i < fields.Length && Complete; i++)
            {
                if (--_budget < 0)
                {
                    Complete = false;
                    return;
                }

                if (i == 20)
                {
                    AddPlaySession(fields[i]);
                }
                else
                {
                    AddString(fields[i], false);
                }
            }
        }

        private void AddPlaySession(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var guid = ProviderString.TryParseGuid(value, out var g) ? g : (Guid?)null;
            Values.Add(new CollectedValue(guid?.ToString("N") ?? value, guid, false, ValueHint.PlaySessionId, value));
        }

        private void AddString(string text, bool isRouteIdentifier)
        {
            if (ProviderString.TryParseGuid(text, out var guid))
            {
                Values.Add(new CollectedValue(guid.ToString("N"), guid, isRouteIdentifier));
            }
            else if (ProviderString.Classify(text, out _) != ProviderStringKind.None || isRouteIdentifier)
            {
                Values.Add(new CollectedValue(text, null, isRouteIdentifier));
            }
        }

        private void WalkJson(JsonElement json, int depth)
        {
            if (--_budget < 0 || depth >= MaxDepth)
            {
                Complete = false;
                return;
            }

            switch (json.ValueKind)
            {
                case JsonValueKind.String:
                    AddString(json.GetString() ?? string.Empty, false);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in json.EnumerateArray())
                    {
                        WalkJson(item, depth + 1);
                    }

                    break;
                case JsonValueKind.Object:
                    foreach (var property in json.EnumerateObject())
                    {
                        WalkJson(property.Value, depth + 1);
                    }

                    break;
            }
        }
    }
}
