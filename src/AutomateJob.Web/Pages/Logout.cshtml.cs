using AutomateJob.Web.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class LogoutModel : PageModel
{
    public IActionResult OnPost()
    {
        Response.Cookies.Delete(JwtTokenService.CookieName);
        return RedirectToPage("/Login");
    }
}
