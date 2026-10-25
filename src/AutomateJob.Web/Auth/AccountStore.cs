using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AutomateJob.Web.Auth;

public sealed record AppUser(long Id, string Username, string Role);

public sealed record RoleRow(long Id, string Name);

public sealed record UserRow(long Id, string Username, string Role, int JobCount);

public sealed record SaveUserResult(string? Error, long? Id)
{
    public static SaveUserResult Fail(string error) => new(error, null);

    public static SaveUserResult Ok(long id) => new(null, id);
}

public sealed class AccountStore
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";
    public const string SuperUserRole = "Super user";
    public const string AccountManagers = AdminRole + "," + SuperUserRole;

    private readonly string _connectionString;
    private readonly AuthOptions _auth;

    public long? SeededUserId { get; }

    public AccountStore(IWebHostEnvironment environment, IOptions<AuthOptions> auth)
        : this(AppHost.DatabasePath(environment.ContentRootPath), auth.Value)
    {
    }

    internal AccountStore(string databasePath, AuthOptions auth)
    {
        var folder = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString;
        _auth = auth;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Roles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Username TEXT NOT NULL UNIQUE,
                PasswordHash TEXT NOT NULL,
                RoleId INTEGER NOT NULL REFERENCES Roles(Id)
            );
            """;
        command.ExecuteNonQuery();
        EnsureRole(connection, AdminRole);
        EnsureRole(connection, UserRole);
        EnsureRole(connection, SuperUserRole);
        var owner = EnsureConfiguredUser(connection);
        SeededUserId = owner;
        if (owner is not null)
        {
            ClaimUnowned(connection, "Jobs", owner.Value);
            ClaimUnowned(connection, "Evaluations", owner.Value);
        }
    }

    public AppUser? Authenticate(string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Users.Id, Users.Username, Users.PasswordHash, Roles.Name
            FROM Users
            INNER JOIN Roles ON Roles.Id = Users.RoleId
            WHERE lower(Users.Username) = lower($username)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$username", username.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var hash = reader.GetString(2);
        if (!PasswordHasher.Verify(password, hash))
        {
            return null;
        }

        return new AppUser(reader.GetInt64(0), reader.GetString(1), reader.GetString(3));
    }

    public IReadOnlyList<RoleRow> ListRoles()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name FROM Roles ORDER BY Name;";
        using var reader = command.ExecuteReader();
        var roles = new List<RoleRow>();
        while (reader.Read())
        {
            roles.Add(new RoleRow(reader.GetInt64(0), reader.GetString(1)));
        }

        return roles;
    }

    public IReadOnlyList<UserRow> ListUsers()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Users.Id, Users.Username, Roles.Name,
                   (SELECT COUNT(*) FROM Jobs WHERE Jobs.UserId = Users.Id)
            FROM Users
            INNER JOIN Roles ON Roles.Id = Users.RoleId
            ORDER BY Users.Username;
            """;
        using var reader = command.ExecuteReader();
        var users = new List<UserRow>();
        while (reader.Read())
        {
            users.Add(new UserRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return users;
    }

    public UserRow? Find(long id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Users.Id, Users.Username, Roles.Name,
                   (SELECT COUNT(*) FROM Jobs WHERE Jobs.UserId = Users.Id)
            FROM Users
            INNER JOIN Roles ON Roles.Id = Users.RoleId
            WHERE Users.Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new UserRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    public SaveUserResult SaveUser(long? id, string? username, string? role, string? password)
    {
        var name = username?.Trim() ?? "";
        var roleName = role?.Trim() ?? "";
        if (name.Length < 2 || name.Length > 80 || name.Any(char.IsWhiteSpace))
        {
            return SaveUserResult.Fail("Enter a username of 2 to 80 characters, with no spaces.");
        }

        var newPassword = string.IsNullOrEmpty(password) ? null : password;
        if (id is null && newPassword is null)
        {
            return SaveUserResult.Fail("Enter a password for the new user.");
        }

        if (newPassword is not null && newPassword.Length < 8)
        {
            return SaveUserResult.Fail("Use a password of at least 8 characters.");
        }

        using var connection = Open();
        var roleId = FindRoleId(connection, roleName);
        if (roleId is null)
        {
            return SaveUserResult.Fail("Choose a role.");
        }

        if (UsernameTaken(connection, name, id))
        {
            return SaveUserResult.Fail("That username is already in use.");
        }

        if (id is long existing)
        {
            var current = FindRoleName(connection, existing);
            if (current is null)
            {
                return SaveUserResult.Fail("That user was not found.");
            }

            if (!string.Equals(current, roleName, StringComparison.Ordinal) && IsLastAdmin(connection, existing))
            {
                return SaveUserResult.Fail("Keep at least one Admin.");
            }

            if (!string.Equals(current, roleName, StringComparison.Ordinal) && IsLastInRole(connection, existing, SuperUserRole))
            {
                return SaveUserResult.Fail("Keep at least one Super user.");
            }

            using var update = connection.CreateCommand();
            update.CommandText = newPassword is null
                ? """
                    UPDATE Users
                    SET Username = $username, RoleId = $role
                    WHERE Id = $id;
                    """
                : """
                    UPDATE Users
                    SET Username = $username, RoleId = $role, PasswordHash = $hash
                    WHERE Id = $id;
                    """;
            update.Parameters.AddWithValue("$username", name);
            update.Parameters.AddWithValue("$role", roleId.Value);
            update.Parameters.AddWithValue("$id", existing);
            if (newPassword is not null)
            {
                update.Parameters.AddWithValue("$hash", PasswordHasher.Hash(newPassword));
            }

            update.ExecuteNonQuery();
            return SaveUserResult.Ok(existing);
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Users (Username, PasswordHash, RoleId)
            VALUES ($username, $hash, $role);
            """;
        insert.Parameters.AddWithValue("$username", name);
        insert.Parameters.AddWithValue("$hash", PasswordHasher.Hash(newPassword!));
        insert.Parameters.AddWithValue("$role", roleId.Value);
        insert.ExecuteNonQuery();
        using var inserted = connection.CreateCommand();
        inserted.CommandText = "SELECT last_insert_rowid();";
        return SaveUserResult.Ok((long)(inserted.ExecuteScalar() ?? 0L));
    }

    private static long? FindRoleId(SqliteConnection connection, string roleName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Roles WHERE Name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", roleName);
        var value = command.ExecuteScalar();
        return value is long id ? id : null;
    }

    private static string? FindRoleName(SqliteConnection connection, long userId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Roles.Name
            FROM Users
            INNER JOIN Roles ON Roles.Id = Users.RoleId
            WHERE Users.Id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", userId);
        return command.ExecuteScalar() as string;
    }

    private static bool UsernameTaken(SqliteConnection connection, string username, long? exceptId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = exceptId is null
            ? "SELECT 1 FROM Users WHERE lower(Username) = lower($username) LIMIT 1;"
            : "SELECT 1 FROM Users WHERE lower(Username) = lower($username) AND Id <> $id LIMIT 1;";
        command.Parameters.AddWithValue("$username", username);
        if (exceptId is not null)
        {
            command.Parameters.AddWithValue("$id", exceptId.Value);
        }

        return command.ExecuteScalar() is not null;
    }

    private static bool IsLastAdmin(SqliteConnection connection, long userId)
    {
        return IsLastInRole(connection, userId, AdminRole);
    }

    private static bool IsLastInRole(SqliteConnection connection, long userId, string role)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM Users
            INNER JOIN Roles ON Roles.Id = Users.RoleId
            WHERE Roles.Name = $role AND Users.Id <> $id;
            """;
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$id", userId);
        var others = (long)(command.ExecuteScalar() ?? 0L);
        if (others > 0)
        {
            return false;
        }

        return string.Equals(FindRoleName(connection, userId), role, StringComparison.Ordinal);
    }

    private long? EnsureConfiguredUser(SqliteConnection connection)
    {
        var username = _auth.Username?.Trim() ?? "";
        var hash = _auth.PasswordHash?.Trim() ?? "";
        if (username.Length == 0 || hash.Length == 0)
        {
            using var any = connection.CreateCommand();
            any.CommandText = "SELECT Id FROM Users ORDER BY Id LIMIT 1;";
            var existing = any.ExecuteScalar();
            return existing is long id ? id : null;
        }

        using var find = connection.CreateCommand();
        find.CommandText = "SELECT Id FROM Users WHERE lower(Username) = lower($username) LIMIT 1;";
        find.Parameters.AddWithValue("$username", username);
        var found = find.ExecuteScalar();
        if (found is long userId)
        {
            using var promote = connection.CreateCommand();
            promote.CommandText = """
                UPDATE Users
                SET RoleId = (SELECT Id FROM Roles WHERE Name = $role)
                WHERE Id = $id;
                """;
            promote.Parameters.AddWithValue("$role", SuperUserRole);
            promote.Parameters.AddWithValue("$id", userId);
            promote.ExecuteNonQuery();
            return userId;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO Users (Username, PasswordHash, RoleId)
            VALUES ($username, $hash, (SELECT Id FROM Roles WHERE Name = $role));
            """;
        insert.Parameters.AddWithValue("$username", username);
        insert.Parameters.AddWithValue("$hash", hash);
        insert.Parameters.AddWithValue("$role", SuperUserRole);
        insert.ExecuteNonQuery();
        using var inserted = connection.CreateCommand();
        inserted.CommandText = "SELECT last_insert_rowid();";
        return (long)(inserted.ExecuteScalar() ?? 0L);
    }

    private static void EnsureRole(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO Roles (Name) VALUES ($name);";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    private static void ClaimUnowned(SqliteConnection connection, string table, long userId)
    {
        using var info = connection.CreateCommand();
        info.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $table;";
        info.Parameters.AddWithValue("$table", table);
        if (info.ExecuteScalar() is null)
        {
            return;
        }

        using var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(" + table + ");";
        var hasUser = false;
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "UserId", StringComparison.OrdinalIgnoreCase))
                {
                    hasUser = true;
                }
            }
        }

        if (!hasUser)
        {
            return;
        }

        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE " + table + " SET UserId = $user WHERE UserId = 0;";
        update.Parameters.AddWithValue("$user", userId);
        update.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
