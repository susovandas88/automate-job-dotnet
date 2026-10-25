using AutomateJob.Web.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class ProfileModel : PageModel
{
    private readonly ApplicantProfileStore _profiles;

    public ProfileModel(ApplicantProfileStore profiles)
    {
        _profiles = profiles;
    }

    [BindProperty]
    public ApplicantProfile Profile { get; set; } = new();

    public string? Message { get; private set; }

    public void OnGet()
    {
        Profile = _profiles.Load();
    }

    public IActionResult OnPost()
    {
        _profiles.Save(Profile);
        Profile = _profiles.Load();
        Message = "Application details saved. These values are kept on this computer for company forms.";
        return Page();
    }
}
