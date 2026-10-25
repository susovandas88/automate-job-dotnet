namespace AutomateJob.Web.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Username { get; set; } = "";

    public string PasswordHash { get; set; } = "";

    public string JwtKey { get; set; } = "";

    public string Issuer { get; set; } = "automate-job";

    public string Audience { get; set; } = "automate-job";
}
