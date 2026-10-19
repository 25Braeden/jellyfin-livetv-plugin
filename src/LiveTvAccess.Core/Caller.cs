using System;

namespace LiveTvAccess.Core;

/// <summary>
/// Who is making a request.
/// </summary>
/// <param name="UserId">The user id; <see cref="Guid.Empty"/> for API-key requests.</param>
/// <param name="IsApiKey">Whether the request was authenticated with an API key.</param>
/// <param name="IsAdministrator">Whether the user is an administrator.</param>
public readonly record struct Caller(Guid UserId, bool IsApiKey, bool IsAdministrator);
