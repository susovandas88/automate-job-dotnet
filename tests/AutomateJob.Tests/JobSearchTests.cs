using AutomateJob.Web.Jobs;
using AutomateJob.Web.Matching;

namespace AutomateJob.Tests;

public class JobSearchTests
{
    [Fact]
    public void Search_suggestion_reads_keywords_and_location()
    {
        var suggestion = LlmJobMatcher.ParseSearchSuggestion("""
            {"keywords":"Backend engineer C#","location":"Bengaluru"}
            """);

        Assert.Equal("Backend engineer C#", suggestion.Keywords);
        Assert.Equal("Bengaluru", suggestion.Location);
    }

    [Fact]
    public void Relevant_jobs_stay_visible_and_weak_jobs_hide()
    {
        var relevant = Sample(80, error: null);
        var weak = Sample(40, error: null);
        var failed = Sample(null, error: "Could not read the description");

        Assert.True(JobRelevance.IsVisible(relevant, showWeakMatches: false));
        Assert.False(JobRelevance.IsVisible(weak, showWeakMatches: false));
        Assert.True(JobRelevance.IsVisible(weak, showWeakMatches: true));
        Assert.True(JobRelevance.IsVisible(failed, showWeakMatches: false));
    }

    private static StoredJob Sample(int? score, string? error)
    {
        return new StoredJob(1, "https://www.linkedin.com/jobs/view/1/", "Role", "Company", "City", "1 week ago · 2 Oct 2026", score, null, ["C#"], ["Kafka"], error);
    }

    [Fact]
    public void Job_list_pages_and_filters_by_posted_date()
    {
        var jobs = Enumerable.Range(1, 30)
            .Select(index => new StoredJob(
                index,
                "https://www.linkedin.com/jobs/view/" + index + "/",
                "Role " + index,
                "Company",
                index <= 10 ? "Bengaluru, Karnataka, India" : "Hyderabad, Telangana, India",
                index <= 10 ? "1 day ago · 10 Oct 2026" : "1 week ago · 2 Oct 2026",
                80,
                null,
                ["C#"],
                [],
                null))
            .ToList();

        var first = JobListQuery.Apply(jobs, true, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10), "bengaluru", 1, out var total, out var pageCount, out var pageNumber);
        Assert.Equal(10, total);
        Assert.Equal(1, pageCount);
        Assert.Equal(1, pageNumber);
        Assert.Equal(10, first.Count);
        Assert.All(first, job => Assert.Contains("10 Oct 2026", job.Posted));

        var second = JobListQuery.Apply(jobs, true, null, null, null, 2, out total, out pageCount, out pageNumber);
        Assert.Equal(30, total);
        Assert.Equal(2, pageCount);
        Assert.Equal(2, pageNumber);
        Assert.Equal(5, second.Count);
        Assert.Equal("Role 26", second[0].Title);
    }

    [Fact]
    public void Evaluation_table_stores_the_score_for_a_job_description()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-eval-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new EvaluationStore(database);
        var description = "Build ASP.NET Core services with C# and Azure.";
        var key = EvaluationStore.KeyForPastedDescription(description);

        store.Save(1, new JobEvaluation(key, "Backend engineer", "Northwind", "Bengaluru", description, 72, "Partial fit", ["C#"], ["Kafka"]));
        store.Save(1, new JobEvaluation(key, "Backend engineer", "Northwind", "Bengaluru", description, 81, "Stronger fit", ["C#", "Azure"], []));

        var saved = store.Find(1, key);

        Assert.NotNull(saved);
        Assert.Equal(81, saved.Score);
        Assert.Equal("Stronger fit", saved.Summary);
        Assert.Equal(["C#", "Azure"], saved.Covered);
        Assert.Equal(description, saved.Description);
        Assert.StartsWith("pasted:", key);
    }

    [Fact]
    public void Stored_resume_can_be_listed_and_read()
    {
        var folder = Path.Combine(Path.GetTempPath(), "resumes-" + Guid.NewGuid().ToString("N"));
        var library = new ResumeLibrary(folder);
        var first = library.Save(1, "Susovan.pdf", "Senior .NET developer");
        var again = library.Save(1, "Copy.pdf", "Senior .NET developer");
        var second = library.Save(1, "Other.txt", "Java developer");
        library.Save(2, "Shared.pdf", "Senior .NET developer");

        Assert.Equal(first, again);
        Assert.Equal(2, library.List(1).Count);
        Assert.Single(library.List(2));
        Assert.Equal("Senior .NET developer", library.Read(1, first));
        Assert.Null(library.Read(2, second));
        Assert.Equal("Java developer", library.Read(1, second));
        Assert.True(library.Delete(2, first));
        Assert.Equal("Senior .NET developer", library.Read(1, first));
        Assert.True(library.Delete(1, second));
        Assert.Null(library.Read(1, second));
        Assert.Single(library.List(1));
        Assert.False(library.Delete(1, second));
    }

    [Fact]
    public void Public_job_list_reads_title_company_and_location()
    {
        const string html = """
            <div data-entity-urn="urn:li:jobPosting:4471381784">
              <h3 class="base-search-card__title">.NET core Senior Developer</h3>
              <h4 class="base-search-card__subtitle">
                <a href="https://www.linkedin.com/company/infosys">Infosys</a>
              </h4>
              <span class="job-search-card__location">Bengaluru East, Karnataka, India</span>
              <time class="job-search-card__listdate" datetime="2026-10-02">1 week ago</time>
            </div>
            <div data-entity-urn="urn:li:jobPosting:4471381785">
              <h3 class="base-search-card__title">Platform Engineer</h3>
              <h4 class="base-search-card__subtitle">
                <a href="https://www.linkedin.com/company/contoso">Contoso</a>
              </h4>
              <span class="job-search-card__location">Hyderabad, Telangana, India</span>
              <time class="job-search-card__listdate" datetime="2026-10-03">6 days ago</time>
              <span class="job-posting-benefits__text">Easy Apply</span>
            </div>
            """;

        var jobs = LinkedInJobBoard.ParseList(html);

        Assert.Equal(2, jobs.Count);
        Assert.Equal(".NET core Senior Developer", jobs[0].Title);
        Assert.Equal("Infosys", jobs[0].Company);
        Assert.Equal("Bengaluru East, Karnataka, India", jobs[0].Location);
        Assert.Equal("https://www.linkedin.com/jobs/view/4471381784/", jobs[0].Url);
        Assert.Equal("1 week ago · 2 Oct 2026", jobs[0].Posted);
        Assert.Equal("2026-10-02", jobs[0].PostedOn);
        Assert.Equal("Apply", jobs[0].ApplyMethod);
        Assert.Equal("Easy Apply", jobs[1].ApplyMethod);
    }

    [Fact]
    public void Naukri_search_url_and_posted_day_are_readable()
    {
        Assert.Equal("https://www.naukri.com/dotnet-jobs-in-bengaluru", NaukriJobBoard.SearchUrl("Dotnet", "Bengaluru", 1));
        Assert.Equal("https://www.naukri.com/dotnet-jobs-in-bengaluru-2", NaukriJobBoard.SearchUrl("Dotnet", "Bengaluru", 2));
        Assert.Equal("2026-10-11", NaukriJobBoard.PostedOn("1 day ago", new DateOnly(2026, 10, 12)));

        var jobs = NaukriJobBoard.ParseCards("""
            [{"url":"https://www.naukri.com/job-listings-demo","title":".NET Developer","company":"Infosys","location":"Bengaluru","posted":"2 days ago"}]
            """);

        Assert.Single(jobs);
        Assert.Equal(".NET Developer", jobs[0].Title);
        Assert.Equal("Infosys", jobs[0].Company);
    }

    [Fact]
    public void Apply_keeps_resume_facts_and_stops_before_submit()
    {
        Assert.False(ApplicationForm.CanApply(60));
        Assert.True(ApplicationForm.CanApply(61));

        var answers = ApplicationForm.Parse("""
            {"answers":[{"label":"Email","value":"ada@example.com"},{"label":"Phone","value":"999"}]}
            """);
        var kept = ApplicationForm.KeepResumeFacts(answers, "Email ada@example.com");

        Assert.Equal("ada@example.com", kept[0].Value);
        Assert.Null(kept[1].Value);
        Assert.Equal("submit", ApplicationForm.ChooseAction(["Next", "Submit application"]));
        Assert.Equal("advance", ApplicationForm.ChooseAction(["Next"]));
    }

    [Fact]
    public void Saved_application_details_fill_email_and_retype_email()
    {
        var fields = new FormField[]
        {
            new("Email Address", "text", [], "0"),
            new("Retype Email Address", "text", [], "1"),
            new("Choose Password", "password", [], "2")
        };
        var answers = ApplicationForm.UseSavedDetails(fields, [], new ApplicantProfile { Email = "ada@example.com" });

        Assert.Equal("ada@example.com", answers[0].Value);
        Assert.Equal("ada@example.com", answers[1].Value);
        Assert.Null(answers[2].Value);
    }

    [Fact]
    public void Application_table_stores_the_filled_status()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-apply-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new ApplicationStore(database);
        store.Save(new JobApplication(
            "https://www.linkedin.com/jobs/view/1/",
            "Backend engineer",
            "Northwind",
            81,
            "Filled",
            ["Email: ada@example.com"],
            ["Phone"],
            "The form is filled up to the review step."));

        var saved = store.Find("https://www.linkedin.com/jobs/view/1/");

        Assert.NotNull(saved);
        Assert.Equal("Filled", saved.Status);
        Assert.Equal(["Email: ada@example.com"], saved.Filled);
        Assert.Equal(["Phone"], saved.Blank);

        store.Save(new JobApplication(
            "https://www.linkedin.com/jobs/view/2/",
            "Backend engineer",
            "Northwind",
            81,
            "Done",
            [],
            [],
            "Applying."));

        var applications = store.ByUrl().Values.ToList();
        Assert.Single(applications);
        Assert.True(ApplicationStore.Matches(applications[0], "https://www.linkedin.com/jobs/view/2/", "Backend engineer", "Northwind"));
        Assert.Equal("Done", applications[0].Status);
    }

    [Fact]
    public void The_same_company_and_posted_date_is_stored_once()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-unique-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new JobStore(database);
        store.SaveListing(1, new LinkedInJob("https://www.linkedin.com/jobs/view/1/", "Custom Software Engineer", "Accenture", "Hyderabad", "", "1 day ago", "2026-10-10"));
        store.SaveListing(1, new LinkedInJob("https://www.linkedin.com/jobs/view/2/", "custom software engineer", "Accenture", "Bengaluru", "", "2 hours ago", "2026-10-10", "Easy Apply"));
        store.SaveListing(1, new LinkedInJob("https://www.linkedin.com/jobs/view/3/", "Data Engineer", "Accenture", "Hyderabad", "", "2 hours ago", "2026-10-10"));
        store.SaveListing(1, new LinkedInJob("https://www.linkedin.com/jobs/view/4/", "Custom Software Engineer", "Accenture", "Hyderabad", "", "1 day ago", "2026-10-11"));

        var jobs = store.List(1);

        Assert.Equal(3, jobs.Count);
        var updated = Assert.Single(jobs, job => job.JobUrl == "https://www.linkedin.com/jobs/view/1/");
        Assert.Equal("Bengaluru", updated.Location);
        Assert.Contains("2 hours ago", updated.Posted);
        Assert.Equal("Easy Apply", updated.ApplyMethod);
        Assert.Contains(jobs, job => job.Title == "Data Engineer");
        Assert.Contains(jobs, job => job.JobUrl.EndsWith("/4/", StringComparison.Ordinal));
    }

    [Fact]
    public void Newest_jobs_are_listed_first_and_the_list_stops_at_1000()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-newest-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new JobStore(database);
        store.SaveListing(1, new LinkedInJob("https://www.naukri.com/job-listings-keep", "Naukri role", "Infosys", "Bengaluru", "", "today", "2026-10-14"), "Naukri");
        var oldest = new DateOnly(2024, 1, 1);
        for (var index = 0; index < JobStore.MaxJobs + 2; index++)
        {
            var posted = oldest.AddDays(index);
            store.SaveListing(1, new LinkedInJob(
                "https://www.linkedin.com/jobs/view/" + index + "/",
                "Role " + index,
                "Northwind",
                "Bengaluru",
                "",
                posted.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
                posted.ToString("yyyy-MM-dd")));
        }

        var jobs = store.ListBySource(1, "LinkedIn");

        Assert.Equal(JobStore.MaxJobs, jobs.Count);
        Assert.Equal(oldest.AddDays(JobStore.MaxJobs + 1).ToString("yyyy-MM-dd"), PostedOnOf(jobs[0]));
        Assert.DoesNotContain(jobs, job => job.JobUrl.EndsWith("/0/", StringComparison.Ordinal));
        Assert.Single(store.ListBySource(1, "Naukri"));
    }

    [Fact]
    public void Each_user_sees_only_their_own_jobs()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-users-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new JobStore(database);
        store.SaveListing(1, new LinkedInJob("https://www.linkedin.com/jobs/view/mine/", "My role", "Northwind", "Bengaluru", "", "today", "2026-10-20"));
        store.SaveListing(2, new LinkedInJob("https://www.linkedin.com/jobs/view/mine/", "Their role", "Contoso", "Pune", "", "today", "2026-10-20"));
        store.SaveListing(2, new LinkedInJob("https://www.linkedin.com/jobs/view/other/", "Other role", "Contoso", "Pune", "", "today", "2026-10-20"));

        var mine = store.List(1);
        var theirs = store.List(2);

        Assert.Single(mine);
        Assert.Equal("My role", mine[0].Title);
        Assert.Equal(2, theirs.Count);
        Assert.DoesNotContain(theirs, job => job.Title == "My role");
    }

    private static string PostedOnOf(StoredJob job)
    {
        var separator = job.Posted.LastIndexOf('·');
        var tail = separator >= 0 ? job.Posted[(separator + 1)..].Trim() : job.Posted.Trim();
        return DateOnly.ParseExact(tail, "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture).ToString("yyyy-MM-dd");
    }

    [Fact]
    public void Applicant_profile_saves_the_details_used_on_company_forms()
    {
        var database = Path.Combine(Path.GetTempPath(), "job-profile-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var store = new ApplicantProfileStore(database);

        store.Save(new ApplicantProfile
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Email = "ada@example.com",
            Phone = "999",
            City = "Bengaluru",
            WorkAuthorization = "Authorized to work in India"
        });
        store.Save(new ApplicantProfile
        {
            FirstName = "Ada",
            LastName = "Byron",
            Email = "ada@example.com",
            City = "Bengaluru"
        });

        var saved = store.Load();

        Assert.Equal("Byron", saved.LastName);
        Assert.Equal("ada@example.com", saved.Email);
        Assert.Equal("", saved.Phone);
        Assert.Equal("Bengaluru", saved.City);
    }
}
