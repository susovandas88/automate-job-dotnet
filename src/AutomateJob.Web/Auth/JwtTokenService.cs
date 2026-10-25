using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AutomateJob.Web.Auth;

public sealed class JwtTokenService(IOptions<AuthOptions> options)
{
    public const string CookieName = "job_match_token";

    public string Create(AppUser user)
    {
        var settings = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.JwtKey));
        var token = new JwtSecurityToken(
            issuer: settings.Issuer,
            audience: settings.Audience,
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            ],
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
