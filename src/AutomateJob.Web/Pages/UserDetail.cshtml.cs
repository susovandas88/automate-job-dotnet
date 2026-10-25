using AutomateJob.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

[Authorize(Roles = AccountStore.AccountManagers)]
public class UserDetailModel(AccountStore accounts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Role { get; set; } = AccountStore.UserRole;

    [BindProperty]
    public string? Password { get; set; }

    public IReadOnlyList<RoleRow> Roles { get; private set; } = [];

    public int JobCount { get; private set; }

    public bool IsNew => Id is null or 0;

    public bool EditingSelf => Id is long id && id == SignedInUser.Id(User);

    public string? Message { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool CanChangePassword => User.IsInRole(AccountStore.SuperUserRole);

    public IActionResult OnGet(int? saved)
    {
        Roles = accounts.ListRoles();
        if (IsNew)
        {
            Role = AccountStore.UserRole;
            return Page();
        }

        var user = accounts.Find(Id!.Value);
        if (user is null)
        {
            return NotFound();
        }

        Username = user.Username;
        Role = user.Role;
        JobCount = user.JobCount;
        if (saved == 1)
        {
            Message = "Saved.";
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        Roles = accounts.ListRoles();
        var existing = IsNew ? null : accounts.Find(Id!.Value);
        if (!IsNew && existing is null)
        {
            return NotFound();
        }

        if (existing is not null)
        {
            JobCount = existing.JobCount;
            if (EditingSelf)
            {
                Role = existing.Role;
            }
        }

        if (!IsNew && !CanChangePassword && !string.IsNullOrEmpty(Password))
        {
            ErrorMessage = "Only a Super user can change a password.";
            Password = "";
            return Page();
        }

        if (!CanChangePassword
            && !string.Equals(existing?.Role, AccountStore.SuperUserRole, StringComparison.Ordinal)
            && string.Equals(Role, AccountStore.SuperUserRole, StringComparison.Ordinal))
        {
            ErrorMessage = "Only a Super user can assign the Super user role.";
            return Page();
        }

        var result = accounts.SaveUser(IsNew ? null : Id, Username, Role, IsNew || CanChangePassword ? Password : null);

        if (result.Error is not null)
        {
            ErrorMessage = result.Error;
            Password = "";
            return Page();
        }

        return RedirectToPage(new { id = result.Id, saved = 1 });
    }
}
