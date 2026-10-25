using Microsoft.Data.Sqlite;

namespace AutomateJob.Web.Jobs;

public sealed class ApplicantProfile
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string City { get; set; } = "";
    public string Country { get; set; } = "";
    public string LinkedInUrl { get; set; } = "";
    public string Website { get; set; } = "";
    public string CurrentTitle { get; set; } = "";
    public string YearsOfExperience { get; set; } = "";
    public string WorkAuthorization { get; set; } = "";
    public string NoticePeriod { get; set; } = "";
    public string ExpectedSalary { get; set; } = "";
    public string WillingToRelocate { get; set; } = "";
    public string Education { get; set; } = "";
    public string WorkHistory { get; set; } = "";
    public string Skills { get; set; } = "";
    public string AdditionalDetails { get; set; } = "";

    public string AsFactSheet()
    {
        return string.Join('\n', new[]
        {
            FirstName, LastName, Email, Phone, City, Country, LinkedInUrl, Website, CurrentTitle,
            YearsOfExperience, WorkAuthorization, NoticePeriod, ExpectedSalary, WillingToRelocate,
            Education, WorkHistory, Skills, AdditionalDetails
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
    }
}

public sealed class ApplicantProfileStore
{
    private readonly string _connectionString;

    public ApplicantProfileStore(IWebHostEnvironment environment)
        : this(AppHost.DatabasePath(environment.ContentRootPath))
    {
    }

    internal ApplicantProfileStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS ApplicantProfile (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                FirstName TEXT NOT NULL,
                LastName TEXT NOT NULL,
                Email TEXT NOT NULL,
                Phone TEXT NOT NULL,
                City TEXT NOT NULL,
                Country TEXT NOT NULL,
                LinkedInUrl TEXT NOT NULL,
                Website TEXT NOT NULL,
                CurrentTitle TEXT NOT NULL,
                YearsOfExperience TEXT NOT NULL,
                WorkAuthorization TEXT NOT NULL,
                NoticePeriod TEXT NOT NULL,
                ExpectedSalary TEXT NOT NULL,
                WillingToRelocate TEXT NOT NULL,
                Education TEXT NOT NULL,
                WorkHistory TEXT NOT NULL,
                Skills TEXT NOT NULL,
                AdditionalDetails TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public ApplicantProfile Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FirstName, LastName, Email, Phone, City, Country, LinkedInUrl, Website,
                   CurrentTitle, YearsOfExperience, WorkAuthorization, NoticePeriod, ExpectedSalary,
                   WillingToRelocate, Education, WorkHistory, Skills, AdditionalDetails
            FROM ApplicantProfile
            WHERE Id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ApplicantProfile();
        }

        return new ApplicantProfile
        {
            FirstName = reader.GetString(0),
            LastName = reader.GetString(1),
            Email = reader.GetString(2),
            Phone = reader.GetString(3),
            City = reader.GetString(4),
            Country = reader.GetString(5),
            LinkedInUrl = reader.GetString(6),
            Website = reader.GetString(7),
            CurrentTitle = reader.GetString(8),
            YearsOfExperience = reader.GetString(9),
            WorkAuthorization = reader.GetString(10),
            NoticePeriod = reader.GetString(11),
            ExpectedSalary = reader.GetString(12),
            WillingToRelocate = reader.GetString(13),
            Education = reader.GetString(14),
            WorkHistory = reader.GetString(15),
            Skills = reader.GetString(16),
            AdditionalDetails = reader.GetString(17)
        };
    }

    public void Save(ApplicantProfile profile)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ApplicantProfile (
                Id, FirstName, LastName, Email, Phone, City, Country, LinkedInUrl, Website,
                CurrentTitle, YearsOfExperience, WorkAuthorization, NoticePeriod, ExpectedSalary,
                WillingToRelocate, Education, WorkHistory, Skills, AdditionalDetails)
            VALUES (
                1, $first, $last, $email, $phone, $city, $country, $linkedin, $website,
                $title, $years, $auth, $notice, $salary, $relocate, $education, $history, $skills, $extra)
            ON CONFLICT(Id) DO UPDATE SET
                FirstName = excluded.FirstName,
                LastName = excluded.LastName,
                Email = excluded.Email,
                Phone = excluded.Phone,
                City = excluded.City,
                Country = excluded.Country,
                LinkedInUrl = excluded.LinkedInUrl,
                Website = excluded.Website,
                CurrentTitle = excluded.CurrentTitle,
                YearsOfExperience = excluded.YearsOfExperience,
                WorkAuthorization = excluded.WorkAuthorization,
                NoticePeriod = excluded.NoticePeriod,
                ExpectedSalary = excluded.ExpectedSalary,
                WillingToRelocate = excluded.WillingToRelocate,
                Education = excluded.Education,
                WorkHistory = excluded.WorkHistory,
                Skills = excluded.Skills,
                AdditionalDetails = excluded.AdditionalDetails;
            """;
        command.Parameters.AddWithValue("$first", Clean(profile.FirstName));
        command.Parameters.AddWithValue("$last", Clean(profile.LastName));
        command.Parameters.AddWithValue("$email", Clean(profile.Email));
        command.Parameters.AddWithValue("$phone", Clean(profile.Phone));
        command.Parameters.AddWithValue("$city", Clean(profile.City));
        command.Parameters.AddWithValue("$country", Clean(profile.Country));
        command.Parameters.AddWithValue("$linkedin", Clean(profile.LinkedInUrl));
        command.Parameters.AddWithValue("$website", Clean(profile.Website));
        command.Parameters.AddWithValue("$title", Clean(profile.CurrentTitle));
        command.Parameters.AddWithValue("$years", Clean(profile.YearsOfExperience));
        command.Parameters.AddWithValue("$auth", Clean(profile.WorkAuthorization));
        command.Parameters.AddWithValue("$notice", Clean(profile.NoticePeriod));
        command.Parameters.AddWithValue("$salary", Clean(profile.ExpectedSalary));
        command.Parameters.AddWithValue("$relocate", Clean(profile.WillingToRelocate));
        command.Parameters.AddWithValue("$education", Clean(profile.Education));
        command.Parameters.AddWithValue("$history", Clean(profile.WorkHistory));
        command.Parameters.AddWithValue("$skills", Clean(profile.Skills));
        command.Parameters.AddWithValue("$extra", Clean(profile.AdditionalDetails));
        command.ExecuteNonQuery();
    }

    private static string Clean(string? value)
    {
        return value?.Trim() ?? "";
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
