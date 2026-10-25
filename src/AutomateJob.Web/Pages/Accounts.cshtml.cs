using AutomateJob.Web.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

[Authorize(Roles = AccountStore.AccountManagers)]
public class AccountsModel(AccountStore accounts) : PageModel
{
    public IReadOnlyList<RoleRow> Roles { get; private set; } = [];

    public IReadOnlyList<UserRow> Users { get; private set; } = [];

    public void OnGet()
    {
        Roles = accounts.ListRoles();
        Users = accounts.ListUsers();
    }
}
