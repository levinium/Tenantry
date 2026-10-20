namespace Tenantry.Core.Auth;

/// <summary>Hands out access tokens for the signed-in admin.</summary>
public interface ITokenSource
{
    /// <summary>The signed-in account's UPN, once known.</summary>
    string? Account { get; }

    /// <param name="scopes">For example <c>https://graph.microsoft.com/.default</c>.</param>
    /// <param name="claims">A claims challenge (decoded JSON) from a 401, such as a demand for
    /// recent MFA. When set, the token source must get a fresh token that satisfies it.</param>
    Task<string> GetTokenAsync(IReadOnlyList<string> scopes, string? claims = null, CancellationToken cancellationToken = default);
}
