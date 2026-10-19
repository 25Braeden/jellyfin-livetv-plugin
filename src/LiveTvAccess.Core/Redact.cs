using System;
using System.Security.Cryptography;
using System.Text;

namespace LiveTvAccess.Core;

/// <summary>
/// Log-safe representations of identifiers we should not log in full.
/// </summary>
public static class Redact
{
    /// <summary>
    /// Gets a short, stable hash of a value (first 8 hex chars of SHA-256), e.g. for open tokens.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>"sha256:xxxxxxxx", or "(none)" for null/empty.</returns>
    public static string ShortHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(none)";
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
    }
}
