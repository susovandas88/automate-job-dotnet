using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace AutomateJob.Web.Auth;

public sealed class PasswordHasher(IOptions<AuthOptions> options)
{
    public bool Matches(string? username, string? password)
    {
        var expectedUser = options.Value.Username?.Trim() ?? "";
        var stored = options.Value.PasswordHash?.Trim() ?? "";
        if (expectedUser.Length == 0 || stored.Length == 0 || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!string.Equals(username.Trim(), expectedUser, StringComparison.Ordinal))
        {
            return false;
        }

        return Verify(password, stored);
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.', 2);
        if (parts.Length != 2 || string.IsNullOrEmpty(password))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[0]);
            expected = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 100_000, HashAlgorithmName.SHA256, 32);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }
}
