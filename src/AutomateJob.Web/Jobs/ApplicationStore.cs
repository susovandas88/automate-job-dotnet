using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AutomateJob.Web.Jobs;

public sealed record JobApplication(
    string JobUrl,
    string Title,
    string Company,
    int Score,
    string Status,
    IReadOnlyList<string> Filled,
    IReadOnlyList<string> Blank,
    string Note);

public sealed class ApplicationStore
{
    private readonly string _connectionString;

    public ApplicationStore(IWebHostEnvironment environment)
        : this(AppHost.DatabasePath(environment.ContentRootPath))
    {
    }

    internal ApplicationStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS Applications (
                JobUrl TEXT NOT NULL UNIQUE,
                Title TEXT NOT NULL,
                Company TEXT NOT NULL,
                Score INTEGER NOT NULL,
                Status TEXT NOT NULL,
                Filled TEXT NOT NULL,
                Blank TEXT NOT NULL,
                Note TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        KeepOneApplicationPerRole(connection);
    }

    private static void KeepOneApplicationPerRole(SqliteConnection connection)
    {
        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM Applications
            WHERE rowid NOT IN (
                SELECT rowid FROM (
                    SELECT rowid,
                           ROW_NUMBER() OVER (
                               PARTITION BY lower(trim(Title)), lower(trim(Company))
                               ORDER BY UpdatedAt DESC, rowid DESC
                           ) AS RowNumber
                    FROM Applications
                )
                WHERE RowNumber = 1
            );
            """;
        delete.ExecuteNonQuery();

        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Applications_Role
            ON Applications (lower(trim(Title)), lower(trim(Company)));
            """;
        index.ExecuteNonQuery();
    }

    public void Save(JobApplication application)
    {
        using var connection = Open();
        using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT JobUrl FROM Applications
            WHERE lower(trim(Title)) = lower(trim($title))
              AND lower(trim(Company)) = lower(trim($company))
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$title", application.Title);
        existing.Parameters.AddWithValue("$company", application.Company);
        var storedUrl = existing.ExecuteScalar() as string;
        var url = string.IsNullOrWhiteSpace(storedUrl) ? application.JobUrl : storedUrl;

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Applications (JobUrl, Title, Company, Score, Status, Filled, Blank, Note, UpdatedAt)
            VALUES ($url, $title, $company, $score, $status, $filled, $blank, $note, $updated)
            ON CONFLICT(JobUrl) DO UPDATE SET
                Title = excluded.Title,
                Company = excluded.Company,
                Score = excluded.Score,
                Status = excluded.Status,
                Filled = excluded.Filled,
                Blank = excluded.Blank,
                Note = excluded.Note,
                UpdatedAt = excluded.UpdatedAt;
            """;
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$title", application.Title);
        command.Parameters.AddWithValue("$company", application.Company);
        command.Parameters.AddWithValue("$score", application.Score);
        command.Parameters.AddWithValue("$status", application.Status);
        command.Parameters.AddWithValue("$filled", JsonSerializer.Serialize(application.Filled));
        command.Parameters.AddWithValue("$blank", JsonSerializer.Serialize(application.Blank));
        command.Parameters.AddWithValue("$note", application.Note);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public static bool Matches(JobApplication application, string jobUrl, string title, string company)
    {
        if (string.Equals(application.JobUrl, jobUrl, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(application.Title.Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(application.Company.Trim(), company.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, JobApplication> ByUrl()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobUrl, Title, Company, Score, Status, Filled, Blank, Note
            FROM Applications;
            """;
        using var reader = command.ExecuteReader();
        var applications = new Dictionary<string, JobApplication>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var application = Read(reader);
            applications[application.JobUrl] = application;
        }

        return applications;
    }

    internal JobApplication? Find(string jobUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobUrl, Title, Company, Score, Status, Filled, Blank, Note
            FROM Applications
            WHERE JobUrl = $url
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$url", jobUrl);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private static JobApplication Read(SqliteDataReader reader)
    {
        return new JobApplication(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.GetString(4),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(5)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(6)) ?? [],
            reader.GetString(7));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
