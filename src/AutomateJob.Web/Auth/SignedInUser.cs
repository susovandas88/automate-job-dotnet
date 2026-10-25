using System.Security.Claims;

namespace AutomateJob.Web.Auth;

public static class SignedInUser
{
    public static long Id(ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(value, out var id) ? id : 0;
    }
}
