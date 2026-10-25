using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AutomateJob.Web.Jobs;

public sealed class EvaluationStore
{
    private readonly string _connectionString;

    public EvaluationStore(IWebHostEnvironment environment)
        : this(AppHost.DatabasePath(environment.ContentRootPath))
    {
    }

    internal EvaluationStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS Evaluations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                JobUrl TEXT NOT NULL UNIQUE,
                Title TEXT NOT NULL,
                Company TEXT NOT NULL,
                Location TEXT NOT NULL,
                Description TEXT NOT NULL,
                Score INTEGER NOT NULL,
                Summary TEXT NULL,
                Covered TEXT NOT NULL,
                Missing TEXT NOT NULL,
                EvaluatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
        EnsureUserColumn(connection);
        KeepOneEvaluationPerRole(connection);
    }

    private static void KeepOneEvaluationPerRole(SqliteConnection connection)
    {
        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX IF EXISTS UX_Evaluations_Role;";
            drop.ExecuteNonQuery();
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM Evaluations
            WHERE Id NOT IN (
                SELECT Id FROM (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY UserId, lower(trim(Title)), lower(trim(Company)), lower(trim(Location))
                               ORDER BY Score DESC, EvaluatedAt DESC, Id DESC
                           ) AS RowNumber
                    FROM Evaluations
                )
                WHERE RowNumber = 1
            );
            """;
        delete.ExecuteNonQuery();

        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Evaluations_Role
            ON Evaluations (UserId, lower(trim(Title)), lower(trim(Company)), lower(trim(Location)));
            """;
        index.ExecuteNonQuery();
    }

    public static string KeyForPastedDescription(string description)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(description.Trim()));
        return "pasted:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Save(long userId, JobEvaluation evaluation)
    {
        using var connection = Open();
        using var existing = connection.CreateCommand();
        existing.CommandText = """
            SELECT JobUrl FROM Evaluations
            WHERE UserId = $user
              AND lower(trim(Title)) = lower(trim($title))
              AND lower(trim(Company)) = lower(trim($company))
              AND lower(trim(Location)) = lower(trim($location))
            LIMIT 1;
            """;
        existing.Parameters.AddWithValue("$user", userId);
        existing.Parameters.AddWithValue("$title", evaluation.Title);
        existing.Parameters.AddWithValue("$company", evaluation.Company);
        existing.Parameters.AddWithValue("$location", evaluation.Location);
        var storedUrl = existing.ExecuteScalar() as string;
        var url = string.IsNullOrWhiteSpace(storedUrl) ? evaluation.JobUrl : storedUrl;

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Evaluations (UserId, JobUrl, Title, Company, Location, Description, Score, Summary, Covered, Missing, EvaluatedAt)
            VALUES ($user, $url, $title, $company, $location, $description, $score, $summary, $covered, $missing, $evaluated)
            ON CONFLICT(JobUrl) DO UPDATE SET
                Title = excluded.Title,
                Company = excluded.Company,
                Location = excluded.Location,
                Description = excluded.Description,
                Score = excluded.Score,
                Summary = excluded.Summary,
                Covered = excluded.Covered,
                Missing = excluded.Missing,
                EvaluatedAt = excluded.EvaluatedAt
            WHERE Evaluations.UserId = excluded.UserId;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$url", url);
        command.Parameters.AddWithValue("$title", evaluation.Title);
        command.Parameters.AddWithValue("$company", evaluation.Company);
        command.Parameters.AddWithValue("$location", evaluation.Location);
        command.Parameters.AddWithValue("$description", evaluation.Description);
        command.Parameters.AddWithValue("$score", evaluation.Score);
        command.Parameters.AddWithValue("$summary", (object?)evaluation.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$covered", JsonSerializer.Serialize(evaluation.Covered));
        command.Parameters.AddWithValue("$missing", JsonSerializer.Serialize(evaluation.Missing));
        command.Parameters.AddWithValue("$evaluated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    internal JobEvaluation? Find(long userId, string jobUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobUrl, Title, Company, Location, Description, Score, Summary, Covered, Missing
            FROM Evaluations
            WHERE UserId = $user AND JobUrl = $url
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$url", jobUrl);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new JobEvaluation(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
            JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? []);
    }

    public IReadOnlyDictionary<string, JobEvaluation> ByUrl(long userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobUrl, Title, Company, Location, Description, Score, Summary, Covered, Missing
            FROM Evaluations
            WHERE UserId = $user;
            """;
        command.Parameters.AddWithValue("$user", userId);
        using var reader = command.ExecuteReader();
        var scores = new Dictionary<string, JobEvaluation>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            var evaluation = new JobEvaluation(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<List<string>>(reader.GetString(7)) ?? [],
                JsonSerializer.Deserialize<List<string>>(reader.GetString(8)) ?? []);
            scores[evaluation.JobUrl] = evaluation;
        }

        return scores;
    }

    private static void EnsureUserColumn(SqliteConnection connection)
    {
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(Evaluations);";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "UserId", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Evaluations ADD COLUMN UserId INTEGER NOT NULL DEFAULT 0;";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
