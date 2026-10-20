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

    /// <summary>
    /// Describes where a URL points without revealing it: <c>scheme://host[:port]</c> for an absolute HTTP(S) URL
    /// (no user info, path or query, which is where providers put credentials), <c>file</c> for a local path.
    /// </summary>
    /// <param name="url">The URL or path.</param>
    /// <returns>The redacted location.</returns>
    public static string Location(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "(none)";
        }

        var text = url.Trim();
        if (text.StartsWith('/') || text.StartsWith('\\') || (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/'))
        {
            return "file";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Scheme))
        {
            return "(unrecognized)";
        }

        if (uri.IsFile)
        {
            return "file";
        }

        if (string.IsNullOrEmpty(uri.Host))
        {
            return "(unrecognized)";
        }

        return uri.IsDefaultPort || uri.Port < 0
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
