using Tenantry.Core.Auth;
using Tenantry.Core.Permissions;

namespace Tenantry.Exchange;

/// <summary>
/// Tokens for the cmdlet runner: Purview (Security &amp; Compliance) calls get a search-only token
/// from <paramref name="purview"/>, which every search, hold and retention cmdlet now needs (it
/// reads cases as well); everything else comes from Tenantry's own sign-in.
/// </summary>
public sealed class PurviewTokens(ITokenSource main, ITokenSource purview) : ITokenSource
{
    public string? Account => main.Account;

    public Task<string> GetTokenAsync(IReadOnlyList<string> scopes, string? claims = null, CancellationToken cancellationToken = default) =>
        scopes.Any(s => s.StartsWith(RestCmdletRunner.ComplianceHost, StringComparison.OrdinalIgnoreCase))
            ? purview.GetTokenAsync([$"{TenantryPermissions.SearchOnlyAudience}/.default"], claims, cancellationToken)
            : main.GetTokenAsync(scopes, claims, cancellationToken);
}
