using AutomateJob.Web.Auth;
using AutomateJob.Web.Jobs;
using AutomateJob.Web.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class JobsModel : PageModel
{
    private const string ApiKeySessionKey = "OpenAiApiKey";

    private readonly LlmJobMatcher _matcher;
    private readonly ResumeTextReader _resumeReader;
    private readonly ResumeLibrary _resumes;
    private readonly LinkedInJobBoard _jobs;
    private readonly JobStore _store;
    private readonly EvaluationStore _evaluations;
    private readonly ApplicationStore _applications;
    private readonly ApplicantProfileStore _profiles;
    private readonly AnswersBank _answers;
    private readonly LinkedInSession _session;
    private readonly JobFetchRunner _fetch;

    private long UserId => SignedInUser.Id(User);

    public JobsModel(
        LlmJobMatcher matcher,
        ResumeTextReader resumeReader,
        ResumeLibrary resumes,
        LinkedInJobBoard jobs,
        JobStore store,
        EvaluationStore evaluations,
        ApplicationStore applications,
        ApplicantProfileStore profiles,
        AnswersBank answers,
        LinkedInSession session,
        JobFetchRunner fetch)
    {
        _matcher = matcher;
        _resumeReader = resumeReader;
        _resumes = resumes;
        _jobs = jobs;
        _store = store;
        _evaluations = evaluations;
        _applications = applications;
        _profiles = profiles;
        _answers = answers;
        _session = session;
        _fetch = fetch;
    }

    [BindProperty]
    public IFormFile? ResumeFile { get; set; }

    [BindProperty]
    public string? ResumeText { get; set; }

    [BindProperty]
    public string? SelectedResumeId { get; set; }

    public IReadOnlyList<StoredResume> Resumes { get; private set; } = [];

    [BindProperty]
    public string? ApiKey { get; set; }

    [BindProperty]
    public string? LangSmithApiKey { get; set; }

    public bool HasLangSmithKey { get; private set; }

    [BindProperty]
    public string? Keywords { get; set; }

    [BindProperty]
    public string? Location { get; set; }

    [BindProperty]
    public string? EvaluateUrl { get; set; }

    [BindProperty]
    public List<string> SelectedJobUrls { get; set; } = [];

    [BindProperty]
    public string? ApplyUrl { get; set; }

    public IReadOnlyDictionary<string, JobApplication> Applications { get; private set; } =
        new Dictionary<string, JobApplication>();

    [BindProperty(SupportsGet = true)]
    public bool ShowWeakMatches { get; set; }

    [BindProperty(SupportsGet = true, Name = "p")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "from")]
    public DateOnly? PostedFrom { get; set; }

    [BindProperty(SupportsGet = true, Name = "to")]
    public DateOnly? PostedTo { get; set; }

    [BindProperty(SupportsGet = true, Name = "place")]
    public string? Place { get; set; }

    public int PageCount { get; private set; } = 1;

    public int TotalJobs { get; private set; }

    public int PageSize => JobListQuery.PageSize;

    public bool HasSavedKey { get; private set; }

    public string? Message { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool FetchRunning { get; private set; }

    public bool FetchFailed { get; private set; }

    public string? FetchMessage { get; private set; }

    public IReadOnlyList<StoredJob> Jobs { get; private set; } = [];

    public void OnGet()
    {
        HasSavedKey = HasKey();
        HasLangSmithKey = HasLangSmith();
        LoadJobs();
    }

    public async Task<IActionResult> OnPostLoadResumeAsync(CancellationToken cancellationToken)
    {
        ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Choose a stored resume, or upload or paste one.";
        }

        LoadJobs();
        return Page();
    }

    public IActionResult OnPostDeleteResume(string? deleteResumeId)
    {
        if (string.IsNullOrWhiteSpace(deleteResumeId) || !_resumes.Delete(UserId, deleteResumeId))
        {
            ErrorMessage = "That stored resume could not be found.";
        }
        else
        {
            if (SelectedResumeId == deleteResumeId)
            {
                SelectedResumeId = null;
                ResumeText = "";
            }

            Message = "Deleted the stored resume.";
        }

        HasSavedKey = HasKey();
        HasLangSmithKey = HasLangSmith();
        LoadJobs();
        return Page();
    }

    public async Task<IActionResult> OnPostSuggestAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume before suggesting a search.";
            LoadJobs();
            return Page();
        }

        try
        {
            var suggestion = await _matcher.SuggestSearchAsync(ResumeText, cancellationToken, apiKey);
            Keywords = suggestion.Keywords;
            Location = suggestion.Location;
            Message = "Review the search, then fetch jobs.";
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "OpenAI could not be reached. Check the network and try again.";
        }

        LoadJobs();
        return Page();
    }

    public async Task<IActionResult> OnPostFetchAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume so each job can be scored.";
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Keywords))
        {
            ErrorMessage = "Add search keywords, or use Suggest search from the resume.";
            LoadJobs();
            return Page();
        }

        if (!_fetch.TryStart(new JobFetchRequest(
            Keywords.Trim(),
            Location?.Trim() ?? "",
            apiKey,
            ResumeText!,
            ShowWeakMatches,
            PostedFrom,
            PostedTo,
            string.IsNullOrWhiteSpace(Place) ? null : Place.Trim(),
            PageNumber,
            SignedInUser.Id(User),
            ResolveLangSmithKey())))
        {
            ErrorMessage = "A LinkedIn fetch is already running. You can keep using the list while it finishes.";
            LoadJobs();
            return Page();
        }

        return RedirectToPage(new
        {
            p = PageNumber,
            from = PostedFrom?.ToString("yyyy-MM-dd"),
            to = PostedTo?.ToString("yyyy-MM-dd"),
            place = Place,
            ShowWeakMatches
        });
    }

    public IActionResult OnGetFetchStatus()
    {
        return new JsonResult(_fetch.Snapshot());
    }

    public IActionResult OnPostCancelFetch()
    {
        _fetch.Cancel();
        return RedirectToPage(new
        {
            p = PageNumber,
            from = PostedFrom?.ToString("yyyy-MM-dd"),
            to = PostedTo?.ToString("yyyy-MM-dd"),
            place = Place,
            ShowWeakMatches
        });
    }

    public async Task<IActionResult> OnPostEvaluateAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (!LlmRoute.CanScore(apiKey))
        {
            ErrorMessage = LlmRoute.MissingScoreKeyMessage();
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume before evaluating a job.";
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(EvaluateUrl))
        {
            ErrorMessage = "Choose a job to evaluate.";
            LoadJobs();
            return Page();
        }

        var job = _store.Find(UserId, EvaluateUrl);
        if (job is null)
        {
            ErrorMessage = "That job is not in the list.";
            LoadJobs();
            return Page();
        }

        var (scored, missed, note) = await EvaluateJobsAsync([job], apiKey, cancellationToken);
        if (scored == 1 && missed == 0)
        {
            var saved = _store.Find(UserId, job.JobUrl);
            Message = $"{job.Title} scored {saved?.Score}. The score is in the Score column.";
        }
        else
        {
            ErrorMessage = note ?? "The score could not be calculated.";
        }

        LoadJobs();
        return Page();
    }

    public async Task<IActionResult> OnPostEvaluateSelectedAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (!LlmRoute.CanScore(apiKey))
        {
            ErrorMessage = LlmRoute.MissingScoreKeyMessage().Replace("a job.", "jobs.", StringComparison.Ordinal);
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume before evaluating jobs.";
            LoadJobs();
            return Page();
        }

        var selected = (SelectedJobUrls ?? [])
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(url => _store.Find(UserId, url))
            .Where(job => job is not null)
            .Cast<StoredJob>()
            .ToList();
        if (selected.Count == 0)
        {
            ErrorMessage = "Select at least one job, then click Evaluate selected.";
            LoadJobs();
            return Page();
        }

        var (scored, missed, note) = await EvaluateJobsAsync(selected, apiKey, cancellationToken);
        if (missed == 0)
        {
            Message = $"Evaluated {scored} selected jobs. The scores are in the Score column.";
        }
        else if (!string.IsNullOrWhiteSpace(note))
        {
            ErrorMessage = $"Evaluated {scored} selected jobs. {missed} could not be scored. {note}";
        }
        else
        {
            Message = $"Evaluated {scored} selected jobs. {missed} could not be scored.";
        }

        LoadJobs();
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        var langsmithApiKey = ResolveLangSmithKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (LlmRoute.AgentOpenAiEnabled())
        {
            if (string.IsNullOrWhiteSpace(apiKey) || !apiKey.Trim().StartsWith("sk-", StringComparison.Ordinal))
            {
                ErrorMessage = "Paste an OpenAI key that starts with sk- above. It is kept for this browser session only.";
                LoadJobs();
                return Page();
            }
        }
        else if (string.IsNullOrWhiteSpace(AgentEnvFile.Value("GEMINI_API_KEY")))
        {
            ErrorMessage = "OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in agent/.env.";
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume before applying.";
            LoadJobs();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ApplyUrl))
        {
            ErrorMessage = "Choose a job to apply to.";
            LoadJobs();
            return Page();
        }

        var job = _store.Find(UserId, ApplyUrl);
        if (job is null)
        {
            ErrorMessage = "That job is not in the saved list.";
            LoadJobs();
            return Page();
        }

        var evaluation = _evaluations.Find(UserId, job.JobUrl);
        var score = job.Score ?? evaluation?.Score;
        if (score is not int applyScore || !ApplicationForm.CanApply(applyScore))
        {
            ErrorMessage = "Apply runs for a job only after Evaluate stores a score above 60.";
            LoadJobs();
            return Page();
        }

        MarkApplied(job, applyScore, "Applying.");
        try
        {
            var profile = _profiles.Load();
            var outcome = await _session.ApplyAsync(job.JobUrl, ResumeText!, profile, apiKey ?? "", langsmithApiKey, _answers.Answered(), cancellationToken);
            foreach (var question in outcome.Unmapped ?? [])
            {
                _answers.Remember(question.Label, "");
            }

            var status = outcome.Status == "Needs you" ? "Needs you" : "Done";
            _applications.Save(new JobApplication(
                job.JobUrl,
                job.Title,
                job.Company,
                applyScore,
                status,
                outcome.Filled,
                outcome.Blank,
                outcome.Note));
            if (outcome.Status == "Needs you")
            {
                ErrorMessage = outcome.Note;
            }
            else
            {
                Message = $"{job.Title}: {outcome.Note}";
            }
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
            MarkApplied(job, applyScore, exception.Message);
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "OpenAI could not be reached. The LinkedIn window is still open.";
            MarkApplied(job, applyScore, ErrorMessage);
        }
        catch (TimeoutException)
        {
            ErrorMessage = "LinkedIn took too long to open the application. Try Apply again.";
            MarkApplied(job, applyScore, ErrorMessage);
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "Apply was cancelled.";
            MarkApplied(job, applyScore, ErrorMessage);
        }
        catch (Exception)
        {
            ErrorMessage = "Apply stopped before the company form was finished. The status is Done. Open the LinkedIn window if it is still on the form.";
            MarkApplied(job, applyScore, ErrorMessage);
        }

        LoadJobs();
        return Page();
    }

    public async Task<IActionResult> OnPostDisplayAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        try
        {
            var (scored, relevant, missed) = await ScoreVisibleAsync(apiKey, cancellationToken);
            if (!LlmRoute.CanScore(apiKey))
            {
                Message = LlmRoute.OpenAiEnabled()
                    ? "The Score column has no stored score for these jobs. Paste an OpenAI key that starts with sk- above."
                    : "The Score column has no stored score for these jobs. OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in the web .env file.";
            }
            else if (string.IsNullOrWhiteSpace(ResumeText))
            {
                Message = "Add your resume, then click Score this page. The score is written into the Score column.";
            }
            else if (scored == 0 && missed == 0)
            {
                Message = "The jobs on this page already have a stored score.";
            }
            else if (missed == 0)
            {
                Message = $"The Score column shows the new scores for {scored} jobs on this page. {relevant} are relevant to the resume.";
            }
            else
            {
                Message = $"Scored {scored} jobs on this page. {missed} could not be scored.";
            }
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "OpenAI could not be reached. Scores already stored are still in the table.";
        }
        catch (TimeoutException)
        {
            ErrorMessage = "LinkedIn took too long on one job. Scores already stored are still in the table.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "Scoring was cancelled. Scores already stored are still in the table.";
        }

        LoadJobs();
        return Page();
    }

    private async Task<(int Scored, int Missed, string? Note)> EvaluateJobsAsync(
        IReadOnlyList<StoredJob> jobs,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        var scored = 0;
        var missed = 0;
        string? note = null;
        foreach (var job in jobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var description = await _jobs.ReadDescriptionAsync(job.JobUrl, cancellationToken);
                var stored = await ScoreAsync(new LinkedInJob(job.JobUrl, job.Title, job.Company, job.Location, description, job.Posted, ""), apiKey, cancellationToken);
                _store.Save(UserId, stored);
                if (stored.Score is int score)
                {
                    _evaluations.Save(UserId, new JobEvaluation(
                        job.JobUrl,
                        stored.Title,
                        stored.Company,
                        stored.Location,
                        description,
                        score,
                        stored.Summary,
                        stored.Covered,
                        stored.Missing));
                    scored++;
                }
                else
                {
                    missed++;
                    note ??= stored.Error;
                }
            }
            catch (InvalidOperationException exception)
            {
                _store.Save(UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], exception.Message));
                missed++;
                note ??= exception.Message;
            }
            catch (HttpRequestException)
            {
                missed++;
                note = "OpenAI could not be reached. Scores already stored are still in the table.";
                break;
            }
            catch (TimeoutException)
            {
                missed++;
                note = "LinkedIn took too long on one job. Scores already stored are still in the table.";
                break;
            }
            catch (TaskCanceledException)
            {
                missed++;
                note = "Evaluation was cancelled. Scores already stored are still in the table.";
                break;
            }
        }

        return (scored, missed, note);
    }

    private async Task<(int Scored, int Relevant, int Missed)> ScoreVisibleAsync(string? apiKey, CancellationToken cancellationToken)
    {
        LoadJobs();
        if (!LlmRoute.CanScore(apiKey) || string.IsNullOrWhiteSpace(ResumeText))
        {
            return (0, 0, 0);
        }

        var scored = 0;
        var relevant = 0;
        var missed = 0;
        foreach (var job in Jobs)
        {
            if (job.Score is not null && string.IsNullOrWhiteSpace(job.Error))
            {
                continue;
            }

            if (!job.JobUrl.Contains("/jobs/view/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var description = await _jobs.ReadDescriptionAsync(job.JobUrl, cancellationToken);
                var stored = await ScoreAsync(new LinkedInJob(job.JobUrl, job.Title, job.Company, job.Location, description, job.Posted, ""), apiKey, cancellationToken);
                _store.Save(UserId, stored);
                if (stored.Score is int score)
                {
                    _evaluations.Save(UserId, new JobEvaluation(
                        job.JobUrl,
                        stored.Title,
                        stored.Company,
                        stored.Location,
                        description,
                        score,
                        stored.Summary,
                        stored.Covered,
                        stored.Missing));
                    scored++;
                    if (score >= JobRelevance.MinimumScore)
                    {
                        relevant++;
                    }
                }
                else
                {
                    missed++;
                }
            }
            catch (InvalidOperationException exception)
            {
                _store.Save(UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], exception.Message));
                missed++;
            }
        }

        return (scored, relevant, missed);
    }

    private async Task<StoredJob> ScoreAsync(LinkedInJob job, string? apiKey, CancellationToken cancellationToken)
    {
        if (job.Description.Length < 80)
        {
            return new StoredJob(0, job.Url, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], "The job description could not be read.");
        }

        try
        {
            var report = await _matcher.MatchAsync(ResumeText!, job.Description, job.Title, cancellationToken, apiKey, ResolveLangSmithKey());
            return new StoredJob(
                0,
                job.Url,
                job.Title,
                job.Company,
                job.Location,
                job.Posted,
                report.Score,
                report.Summary,
                report.Matched,
                report.Missing,
                null);
        }
        catch (InvalidOperationException exception)
        {
            return new StoredJob(0, job.Url, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], exception.Message);
        }
    }

    private async Task<bool> TryReadResumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (ResumeFile is { Length: > 0 })
            {
                ResumeText = await _resumeReader.ReadAsync(ResumeFile, cancellationToken);
                SelectedResumeId = _resumes.Save(UserId, ResumeFile.FileName, ResumeText);
            }
            else if (!string.IsNullOrWhiteSpace(SelectedResumeId))
            {
                ResumeText = _resumes.Read(UserId, SelectedResumeId) ?? throw new InvalidOperationException("That stored resume could not be found.");
            }
            else if (!string.IsNullOrWhiteSpace(ResumeText))
            {
                SelectedResumeId = _resumes.Save(UserId, ResumeLibrary.NameFromText(ResumeText), ResumeText);
            }

            return true;
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
            LoadJobs();
            return false;
        }
        catch (Exception)
        {
            ErrorMessage = "The resume file could not be read. Use a text-based PDF, DOCX, or TXT file.";
            LoadJobs();
            return false;
        }
    }

    private string? ResolvePostedApiKey()
    {
        ResolveLangSmithKey();
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            var trimmed = ApiKey.Trim().Trim('"').Trim('\'');
            ApiKey = null;
            if (!trimmed.StartsWith("sk-", StringComparison.Ordinal))
            {
                HttpContext.Session.Remove(ApiKeySessionKey);
                HasSavedKey = false;
                return trimmed;
            }

            HttpContext.Session.SetString(ApiKeySessionKey, trimmed);
            HasSavedKey = true;
            return trimmed;
        }

        var saved = HttpContext.Session.GetString(ApiKeySessionKey);
        if (!string.IsNullOrWhiteSpace(saved) && !saved.StartsWith("sk-", StringComparison.Ordinal))
        {
            HttpContext.Session.Remove(ApiKeySessionKey);
            HasSavedKey = false;
            return null;
        }

        HasSavedKey = !string.IsNullOrWhiteSpace(saved);
        return saved;
    }

    private const string LangSmithSessionKey = "LangSmithApiKey";

    private string? ResolveLangSmithKey()
    {
        if (!string.IsNullOrWhiteSpace(LangSmithApiKey))
        {
            var trimmed = LangSmithApiKey.Trim().Trim('"').Trim('\'');
            LangSmithApiKey = null;
            if (!trimmed.StartsWith("lsv2_", StringComparison.Ordinal))
            {
                HttpContext.Session.Remove(LangSmithSessionKey);
                HasLangSmithKey = false;
                return AgentEnvFile.LangSmithKey();
            }

            HttpContext.Session.SetString(LangSmithSessionKey, trimmed);
            HasLangSmithKey = true;
            return trimmed;
        }

        var saved = HttpContext.Session.GetString(LangSmithSessionKey);
        if (!string.IsNullOrWhiteSpace(saved) && !saved.StartsWith("lsv2_", StringComparison.Ordinal))
        {
            HttpContext.Session.Remove(LangSmithSessionKey);
            HasLangSmithKey = false;
            return AgentEnvFile.LangSmithKey();
        }

        HasLangSmithKey = !string.IsNullOrWhiteSpace(saved);
        return string.IsNullOrWhiteSpace(saved) ? AgentEnvFile.LangSmithKey() : saved;
    }

    private bool HasLangSmith()
    {
        return !string.IsNullOrWhiteSpace(HttpContext.Session.GetString(LangSmithSessionKey));
    }

    private bool HasKey()
    {
        return !string.IsNullOrWhiteSpace(HttpContext.Session.GetString(ApiKeySessionKey));
    }

    public string PageLink(int page)
    {
        var query = new List<string> { "p=" + page };
        if (PostedFrom is not null)
        {
            query.Add("from=" + PostedFrom.Value.ToString("yyyy-MM-dd"));
        }

        if (PostedTo is not null)
        {
            query.Add("to=" + PostedTo.Value.ToString("yyyy-MM-dd"));
        }

        if (!string.IsNullOrWhiteSpace(Place))
        {
            query.Add("place=" + Uri.EscapeDataString(Place.Trim()));
        }

        if (ShowWeakMatches)
        {
            query.Add("ShowWeakMatches=true");
        }

        return "/Jobs?" + string.Join("&", query);
    }

    public IReadOnlyList<int> VisiblePages()
    {
        var start = Math.Max(1, PageNumber - 3);
        var end = Math.Min(PageCount, start + 6);
        start = Math.Max(1, end - 6);
        return Enumerable.Range(start, Math.Max(0, end - start + 1)).ToList();
    }

    private void LoadJobs()
    {
        if (PostedFrom is not null && PostedTo is not null && PostedFrom > PostedTo)
        {
            (PostedFrom, PostedTo) = (PostedTo, PostedFrom);
        }

        Place = string.IsNullOrWhiteSpace(Place) ? null : Place.Trim();
        var jobs = JobListQuery.Apply(_store.ListBySource(UserId, "LinkedIn"), ShowWeakMatches, PostedFrom, PostedTo, Place, PageNumber, out var total, out var pageCount, out var pageNumber);
        var scores = _evaluations.ByUrl(UserId);
        Jobs = jobs.Select(job => ApplyStoredScore(job, scores)).ToList();
        TotalJobs = total;
        PageCount = pageCount;
        PageNumber = pageNumber;
        Resumes = _resumes.List(UserId);
        Applications = _applications.ByUrl();
        var fetch = _fetch.Snapshot();
        FetchRunning = fetch.Running;
        FetchFailed = fetch.Failed;
        FetchMessage = string.IsNullOrWhiteSpace(fetch.Message) ? null : fetch.Message;
    }

    public string ApplyStatus(StoredJob job)
    {
        return FindApplication(job)?.Status ?? "—";
    }

    private void MarkApplied(StoredJob job, int score, string note)
    {
        _applications.Save(new JobApplication(job.JobUrl, job.Title, job.Company, score, "Done", [], [], note));
    }

    public string? ApplicationNote(StoredJob job)
    {
        if (FindApplication(job) is not JobApplication application)
        {
            return null;
        }

        var blanks = application.Blank.Count == 0
            ? ""
            : " Left blank: " + string.Join(", ", application.Blank) + ".";
        return application.Status + ". " + application.Note + blanks;
    }

    private JobApplication? FindApplication(StoredJob job)
    {
        return Applications.Values.FirstOrDefault(application =>
            ApplicationStore.Matches(application, job.JobUrl, job.Title, job.Company));
    }

    private static StoredJob ApplyStoredScore(StoredJob job, IReadOnlyDictionary<string, JobEvaluation> scores)
    {
        if (job.Score is not null || !scores.TryGetValue(job.JobUrl, out var evaluation))
        {
            return job;
        }

        return job with
        {
            Score = evaluation.Score,
            Summary = evaluation.Summary,
            Covered = evaluation.Covered,
            Missing = evaluation.Missing,
            Error = null
        };
    }
}
