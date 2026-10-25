using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AutomateJob.Web.Jobs;

public sealed class JobStore
{
    public const int MaxJobs = 1000;

    private readonly string _connectionString;

    public JobStore(IWebHostEnvironment environment)
        : this(AppHost.DatabasePath(environment.ContentRootPath))
    {
    }

    internal JobStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS Jobs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL DEFAULT 0,
                JobUrl TEXT NOT NULL,
                Title TEXT NOT NULL,
                Company TEXT NOT NULL,
                Location TEXT NOT NULL,
                Posted TEXT NOT NULL DEFAULT '',
                PostedOn TEXT NOT NULL DEFAULT '',
                Score INTEGER NULL,
                Summary TEXT NULL,
                Covered TEXT NOT NULL,
                Missing TEXT NOT NULL,
                Error TEXT NULL,
                FetchedAt TEXT NOT NULL,
                UNIQUE (UserId, JobUrl)
            );
            """;
        command.ExecuteNonQuery();
        UseWal(connection);
        EnsureColumn(connection, "Posted", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "PostedOn", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "Source", "TEXT NOT NULL DEFAULT 'LinkedIn'");
        EnsureColumn(connection, "ApplyMethod", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "UserId", "INTEGER NOT NULL DEFAULT 0");
        RebuildWhenJobUrlIsGlobal(connection);
        using (var fill = connection.CreateCommand())
        {
            fill.CommandText = "UPDATE Jobs SET Source = 'LinkedIn' WHERE trim(Source) = '';";
            fill.ExecuteNonQuery();
        }

        using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP INDEX IF EXISTS UX_Jobs_Role;";
            drop.ExecuteNonQuery();
        }

        KeepOneJobPerRole(connection);
        KeepNewest(connection);
    }

    public void SaveListing(long userId, LinkedInJob job, string source = "LinkedIn")
    {
        using var connection = Open();
        if (UpdateExistingRole(connection, userId, job.Title, job.Company, job.Location, job.Posted, job.PostedOn, source, job.ApplyMethod))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Jobs (UserId, JobUrl, Title, Company, Location, Posted, PostedOn, ApplyMethod, Covered, Missing, FetchedAt, Source)
            VALUES ($user, $url, $title, $company, $location, $posted, $postedOn, $apply, '[]', '[]', $fetched, $source)
            ON CONFLICT(UserId, JobUrl) DO UPDATE SET
                Title = excluded.Title,
                Company = excluded.Company,
                Location = excluded.Location,
                Posted = excluded.Posted,
                PostedOn = excluded.PostedOn,
                ApplyMethod = excluded.ApplyMethod;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$url", job.Url);
        command.Parameters.AddWithValue("$title", job.Title.Trim());
        command.Parameters.AddWithValue("$company", job.Company.Trim());
        command.Parameters.AddWithValue("$location", job.Location.Trim());
        command.Parameters.AddWithValue("$posted", job.Posted);
        command.Parameters.AddWithValue("$postedOn", job.PostedOn);
        command.Parameters.AddWithValue("$apply", job.ApplyMethod);
        command.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
        KeepNewest(connection);
    }

    public bool NeedsScore(long userId, string jobUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Score, Error FROM Jobs WHERE UserId = $user AND JobUrl = $url LIMIT 1;";
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$url", jobUrl);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return true;
        }

        var scoreMissing = reader.IsDBNull(0);
        var error = reader.IsDBNull(1) ? null : reader.GetString(1);
        return scoreMissing || !string.IsNullOrWhiteSpace(error);
    }

    public void Save(long userId, StoredJob job)
    {
        using var connection = Open();
        using var find = connection.CreateCommand();
        var source = job.JobUrl.Contains("naukri.com", StringComparison.OrdinalIgnoreCase) ? "Naukri" : "LinkedIn";
        find.CommandText = """
            SELECT Id FROM Jobs
            WHERE UserId = $user
              AND lower(trim(Title)) = lower(trim($title))
              AND lower(trim(Company)) = lower(trim($company))
              AND PostedOn = $postedOn
              AND Source = $source
            LIMIT 1;
            """;
        find.Parameters.AddWithValue("$user", userId);
        find.Parameters.AddWithValue("$title", job.Title);
        find.Parameters.AddWithValue("$company", job.Company);
        find.Parameters.AddWithValue("$postedOn", PostedOn(job.Posted));
        find.Parameters.AddWithValue("$source", source);
        var existing = find.ExecuteScalar();
        if (existing is not null and not DBNull)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE Jobs
                SET Posted = $posted,
                    PostedOn = $postedOn,
                    Score = $score,
                    Summary = $summary,
                    Covered = $covered,
                    Missing = $missing,
                    Error = $error,
                    FetchedAt = $fetched
                WHERE Id = $id;
                """;
            update.Parameters.AddWithValue("$posted", job.Posted);
            update.Parameters.AddWithValue("$postedOn", PostedOn(job.Posted));
            update.Parameters.AddWithValue("$score", (object?)job.Score ?? DBNull.Value);
            update.Parameters.AddWithValue("$summary", (object?)job.Summary ?? DBNull.Value);
            update.Parameters.AddWithValue("$covered", JsonSerializer.Serialize(job.Covered));
            update.Parameters.AddWithValue("$missing", JsonSerializer.Serialize(job.Missing));
            update.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
            update.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", existing);
            update.ExecuteNonQuery();
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Jobs (UserId, JobUrl, Title, Company, Location, Posted, PostedOn, Score, Summary, Covered, Missing, Error, FetchedAt, Source)
            VALUES ($user, $url, $title, $company, $location, $posted, $postedOn, $score, $summary, $covered, $missing, $error, $fetched, $source)
            ON CONFLICT(UserId, JobUrl) DO UPDATE SET
                Title = excluded.Title,
                Company = excluded.Company,
                Location = excluded.Location,
                Posted = excluded.Posted,
                PostedOn = excluded.PostedOn,
                Score = excluded.Score,
                Summary = excluded.Summary,
                Covered = excluded.Covered,
                Missing = excluded.Missing,
                Error = excluded.Error,
                FetchedAt = excluded.FetchedAt;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$url", job.JobUrl);
        command.Parameters.AddWithValue("$title", job.Title);
        command.Parameters.AddWithValue("$company", job.Company);
        command.Parameters.AddWithValue("$location", job.Location);
        command.Parameters.AddWithValue("$posted", job.Posted);
        command.Parameters.AddWithValue("$postedOn", PostedOn(job.Posted));
        command.Parameters.AddWithValue("$score", (object?)job.Score ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)job.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$covered", JsonSerializer.Serialize(job.Covered));
        command.Parameters.AddWithValue("$missing", JsonSerializer.Serialize(job.Missing));
        command.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$source", source);
        command.ExecuteNonQuery();
        KeepNewest(connection);
    }

    public IReadOnlyList<StoredJob> ListBySource(long userId, string source)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, JobUrl, Title, Company, Location, Posted, Score, Summary, Covered, Missing, Error, ApplyMethod
            FROM Jobs
            WHERE UserId = $user AND Source = $source
            ORDER BY CASE WHEN trim(PostedOn) = '' THEN 1 ELSE 0 END, PostedOn DESC, FetchedAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$user", userId);
        command.Parameters.AddWithValue("$source", source);
        return ReadJobs(command);
    }

    public IReadOnlyList<StoredJob> List(long userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, JobUrl, Title, Company, Location, Posted, Score, Summary, Covered, Missing, Error, ApplyMethod
            FROM Jobs
            WHERE UserId = $user
            ORDER BY CASE WHEN trim(PostedOn) = '' THEN 1 ELSE 0 END, PostedOn DESC, FetchedAt DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$user", userId);
        return ReadJobs(command);
    }

    public StoredJob? Find(long userId, string jobUrl)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, JobUrl, Title, Company, Location, Posted, Score, Summary, Covered, Missing, Error, ApplyMethod
            FROM Jobs
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

        return new StoredJob(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? "" : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ReadList(reader.GetString(8)),
            ReadList(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : "");
    }

    private static bool UpdateExistingRole(SqliteConnection connection, long userId, string title, string company, string location, string posted, string postedOn, string source, string applyMethod)
    {
        using var find = connection.CreateCommand();
        find.CommandText = """
            SELECT Id FROM Jobs
            WHERE UserId = $user
              AND lower(trim(Title)) = lower(trim($title))
              AND lower(trim(Company)) = lower(trim($company))
              AND PostedOn = $postedOn
              AND Source = $source
            LIMIT 1;
            """;
        find.Parameters.AddWithValue("$user", userId);
        find.Parameters.AddWithValue("$title", title);
        find.Parameters.AddWithValue("$company", company);
        find.Parameters.AddWithValue("$postedOn", postedOn);
        find.Parameters.AddWithValue("$source", source);
        var id = find.ExecuteScalar();
        if (id is null or DBNull)
        {
            return false;
        }

        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE Jobs
            SET Title = $title,
                Location = $location,
                Posted = $posted,
                PostedOn = $postedOn,
                ApplyMethod = $apply,
                FetchedAt = $fetched
            WHERE Id = $id;
            """;
        update.Parameters.AddWithValue("$title", title.Trim());
        update.Parameters.AddWithValue("$location", location.Trim());
        update.Parameters.AddWithValue("$posted", posted);
        update.Parameters.AddWithValue("$postedOn", postedOn);
        update.Parameters.AddWithValue("$apply", applyMethod);
        update.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        update.ExecuteNonQuery();
        return true;
    }

    private static void KeepOneJobPerRole(SqliteConnection connection)
    {
        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM Jobs
            WHERE Id NOT IN (
                SELECT Id FROM (
                    SELECT Id,
                           ROW_NUMBER() OVER (
                               PARTITION BY UserId, Source, lower(trim(Title)), lower(trim(Company)), PostedOn
                               ORDER BY CASE WHEN Score IS NULL THEN 0 ELSE 1 END DESC,
                                        Score DESC,
                                        FetchedAt DESC,
                                        Id DESC
                           ) AS RowNumber
                    FROM Jobs
                )
                WHERE RowNumber = 1
            );
            """;
        delete.ExecuteNonQuery();

        using var index = connection.CreateCommand();
        index.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS UX_Jobs_Role
            ON Jobs (UserId, Source, lower(trim(Title)), lower(trim(Company)), PostedOn);
            """;
        index.ExecuteNonQuery();
    }

    private static void KeepNewest(SqliteConnection connection)
    {
        var sources = new List<string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT DISTINCT UserId, Source FROM Jobs;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                sources.Add(reader.GetInt64(0) + "\n" + reader.GetString(1));
            }
        }

        foreach (var pair in sources)
        {
            var split = pair.Split('\n', 2);
            using var delete = connection.CreateCommand();
            delete.CommandText = """
                DELETE FROM Jobs
                WHERE UserId = $user
                  AND Source = $source
                  AND Id NOT IN (
                      SELECT Id FROM Jobs
                      WHERE UserId = $user AND Source = $source
                      ORDER BY CASE WHEN trim(PostedOn) = '' THEN 1 ELSE 0 END,
                               PostedOn DESC,
                               FetchedAt DESC,
                               Id DESC
                      LIMIT $limit
                  );
                """;
            delete.Parameters.AddWithValue("$user", long.Parse(split[0]));
            delete.Parameters.AddWithValue("$source", split[1]);
            delete.Parameters.AddWithValue("$limit", MaxJobs);
            delete.ExecuteNonQuery();
        }
    }

    private static void RebuildWhenJobUrlIsGlobal(SqliteConnection connection)
    {
        if (!HasGlobalJobUrlUnique(connection))
        {
            return;
        }

        using var rebuild = connection.CreateCommand();
        rebuild.CommandText = """
            DROP TABLE IF EXISTS Jobs_owned;
            CREATE TABLE Jobs_owned (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL DEFAULT 0,
                JobUrl TEXT NOT NULL,
                Title TEXT NOT NULL,
                Company TEXT NOT NULL,
                Location TEXT NOT NULL,
                Posted TEXT NOT NULL DEFAULT '',
                PostedOn TEXT NOT NULL DEFAULT '',
                Score INTEGER NULL,
                Summary TEXT NULL,
                Covered TEXT NOT NULL,
                Missing TEXT NOT NULL,
                Error TEXT NULL,
                FetchedAt TEXT NOT NULL,
                Source TEXT NOT NULL DEFAULT 'LinkedIn',
                ApplyMethod TEXT NOT NULL DEFAULT '',
                UNIQUE (UserId, JobUrl)
            );
            INSERT INTO Jobs_owned (Id, UserId, JobUrl, Title, Company, Location, Posted, PostedOn, Score, Summary, Covered, Missing, Error, FetchedAt, Source, ApplyMethod)
            SELECT Id, UserId, JobUrl, Title, Company, Location, Posted, PostedOn, Score, Summary, Covered, Missing, Error, FetchedAt,
                   CASE WHEN trim(Source) = '' THEN 'LinkedIn' ELSE Source END,
                   ApplyMethod
            FROM Jobs;
            DROP TABLE Jobs;
            ALTER TABLE Jobs_owned RENAME TO Jobs;
            """;
        rebuild.ExecuteNonQuery();
    }

    private static bool HasGlobalJobUrlUnique(SqliteConnection connection)
    {
        using var list = connection.CreateCommand();
        list.CommandText = "SELECT name FROM pragma_index_list('Jobs') WHERE \"unique\" = 1;";
        using var reader = list.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        reader.Close();
        foreach (var name in names)
        {
            using var info = connection.CreateCommand();
            info.CommandText = "SELECT name FROM pragma_index_info($name);";
            info.Parameters.AddWithValue("$name", name);
            using var columns = info.ExecuteReader();
            var columnNames = new List<string>();
            while (columns.Read())
            {
                if (columns.IsDBNull(0))
                {
                    columnNames.Add("");
                    continue;
                }

                columnNames.Add(columns.GetString(0));
            }

            if (columnNames.Count == 1 && columnNames[0].Equals("JobUrl", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(Jobs);";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE Jobs ADD COLUMN " + name + " " + definition + ";";
        alter.ExecuteNonQuery();
    }

    private static string PostedOn(string posted)
    {
        var separator = posted.LastIndexOf('·');
        var tail = separator >= 0 ? posted[(separator + 1)..].Trim() : posted;
        if (DateTime.TryParseExact(tail, "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd");
        }

        return NaukriJobBoard.PostedOn(posted, DateOnly.FromDateTime(DateTime.Today));
    }

    private static IReadOnlyList<StoredJob> ReadJobs(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var jobs = new List<StoredJob>();
        while (reader.Read())
        {
            jobs.Add(new StoredJob(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                ReadList(reader.GetString(8)),
                ReadList(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : ""));
        }

        return jobs;
    }

    private static IReadOnlyList<string> ReadList(string json)
    {
        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void UseWal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteScalar();
    }
}
