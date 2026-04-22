using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace KuSaFeBackend.Controllers;

internal static class ControllerAuthExtensions
{
    public static Guid? GetCurrentUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    public static bool IsCurrentUserAdmin(this ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue("isAdmin"), "true", StringComparison.OrdinalIgnoreCase);
}
