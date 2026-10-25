using AutomateJob.Web.Auth;

namespace AutomateJob.Tests;

public class AccountStoreTests
{
    [Fact]
    public void Admin_can_add_a_user_and_edit_that_user()
    {
        var database = Path.Combine(Path.GetTempPath(), "accounts-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new AccountStore(database, new AuthOptions());

        var created = store.SaveUser(null, "riya", AccountStore.UserRole, "password1");
        Assert.Null(created.Error);
        Assert.NotNull(store.Authenticate("riya", "password1"));

        var taken = store.SaveUser(null, "Riya", AccountStore.AdminRole, "password1");
        Assert.Equal("That username is already in use.", taken.Error);

        var edited = store.SaveUser(created.Id, "riya.das", AccountStore.AdminRole, "");
        Assert.Null(edited.Error);
        var account = store.Authenticate("riya.das", "password1");
        Assert.NotNull(account);
        Assert.Equal("riya.das", account.Username);
        Assert.Equal(AccountStore.AdminRole, account.Role);

        var demoted = store.SaveUser(edited.Id, "riya.das", AccountStore.UserRole, null);
        Assert.Equal("Keep at least one Admin.", demoted.Error);
    }

    [Fact]
    public void Super_user_can_change_another_users_password()
    {
        var database = Path.Combine(Path.GetTempPath(), "accounts-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new AccountStore(database, new AuthOptions { Username = "root", PasswordHash = PasswordHasher.Hash("super-pass") });

        var root = store.Authenticate("root", "super-pass");
        Assert.NotNull(root);
        Assert.Equal(AccountStore.SuperUserRole, root.Role);

        var created = store.SaveUser(null, "riya", AccountStore.UserRole, "password1");
        var changed = store.SaveUser(created.Id, "riya", AccountStore.UserRole, "password2");
        Assert.Null(changed.Error);
        Assert.Null(store.Authenticate("riya", "password1"));
        Assert.NotNull(store.Authenticate("riya", "password2"));
    }
}
