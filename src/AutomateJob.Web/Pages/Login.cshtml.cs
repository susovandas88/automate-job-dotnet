using AutomateJob.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

[AllowAnonymous]
public class LoginModel(AccountStore accounts, JwtTokenService tokens) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet(string? returnUrl)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Jobs");
        }

        ReturnUrl = LocalReturn(returnUrl);
        return Page();
    }

    public IActionResult OnPost(string? returnUrl)
    {
        ReturnUrl = LocalReturn(returnUrl);
        var account = accounts.Authenticate(Username, Password);
        if (account is null)
        {
            ErrorMessage = "That username or password is not valid.";
            return Page();
        }

        Response.Cookies.Append(JwtTokenService.CookieName, tokens.Create(account), new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromHours(8)
        });
        return LocalRedirect(ReturnUrl ?? "/Jobs");
    }

    private static string? LocalReturn(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/') || returnUrl.StartsWith("//"))
        {
            return "/Jobs";
        }

        return returnUrl;
    }
}
