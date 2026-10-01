using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;

namespace RetailFlow.Api.Authentication;

/// <summary>
/// Keycloak puts realm roles inside a nested "realm_access": { "roles": [...] }
/// claim rather than as individual standard role claims, so
/// [Authorize(Roles = "...")] / RequireRole() would silently never match without
/// this: it unpacks that JSON blob into one ClaimTypes.Role claim per role, once
/// per authenticated request.
/// </summary>
public sealed class KeycloakRoleClaimsTransformation : IClaimsTransformation
{
    private const string RealmAccessClaimType = "realm_access";

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity)
        {
            return Task.FromResult(principal);
        }

        // Re-entrant per request in some pipelines - don't add duplicate role claims.
        if (identity.HasClaim(claim => claim.Type == ClaimTypes.Role))
        {
            return Task.FromResult(principal);
        }

        var realmAccessClaim = identity.FindFirst(RealmAccessClaimType);
        if (realmAccessClaim is null)
        {
            return Task.FromResult(principal);
        }

        using var realmAccess = JsonDocument.Parse(realmAccessClaim.Value);
        if (!realmAccess.RootElement.TryGetProperty("roles", out var roles))
        {
            return Task.FromResult(principal);
        }

        foreach (var role in roles.EnumerateArray())
        {
            var roleName = role.GetString();
            if (!string.IsNullOrWhiteSpace(roleName))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
            }
        }

        return Task.FromResult(principal);
    }
}
