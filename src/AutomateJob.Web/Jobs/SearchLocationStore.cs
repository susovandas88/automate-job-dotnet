using Microsoft.Data.Sqlite;

namespace AutomateJob.Web.Jobs;

public sealed class SearchLocationStore
{
    private readonly string _connectionString;

    public SearchLocationStore(IWebHostEnvironment environment)
        : this(AppHost.DatabasePath(environment.ContentRootPath))
    {
    }

    internal SearchLocationStore(string databasePath)
    {
        var folder = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ConnectionString;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SearchLocations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                Source TEXT NOT NULL,
                Location TEXT NOT NULL,
                SavedAt TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_SearchLocations
            ON SearchLocations (UserId, Source, Location COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    public void Remember(long userId, string source, string? location)
    {
        var trimmed = location?.Trim() ?? "";
        if (trimmed.Length == 0 || string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        if (trimmed.Length > 120)
        {
            trimmed = trimmed[..120];
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SearchLocations (UserId, Source, Location, SavedAt)
            VALUES ($user, $source, $location, $saved)
            ON CONFLICT(UserId, Source, Location) DO UPDATE SET
                Location = excluded.Location,
                SavedAt = excluded.SavedAt;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$source", source.Trim());
        command.Parameters.AddWithValue("$location", trimmed);
        command.Parameters.AddWithValue("$saved", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> List(long userId, string source, string? selected = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Location FROM SearchLocations
            WHERE UserId = $user AND Source = $source
            ORDER BY Location COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$source", source.Trim());
        using var reader = command.ExecuteReader();
        var places = new List<string>();
        while (reader.Read())
        {
            places.Add(reader.GetString(0));
        }

        if (!string.IsNullOrWhiteSpace(selected)
            && !places.Contains(selected.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            places.Add(selected.Trim());
            places.Sort(StringComparer.OrdinalIgnoreCase);
        }

        return places;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
