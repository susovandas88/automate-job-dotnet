using AutomateJob.Web.Auth;
using AutomateJob.Web.Jobs;
using AutomateJob.Web.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class NaukriModel : PageModel
{
    private const string ApiKeySessionKey = "OpenAiApiKey";

    private readonly LlmJobMatcher _matcher;
    private readonly ResumeTextReader _resumeReader;
    private readonly ResumeLibrary _resumes;
    private readonly NaukriJobBoard _jobs;
    private readonly JobStore _store;
    private readonly EvaluationStore _evaluations;
    private readonly NaukriFetchRunner _fetch;
    private readonly SearchLocationStore _searches;

    private long UserId => SignedInUser.Id(User);

    public NaukriModel(
        LlmJobMatcher matcher,
        ResumeTextReader resumeReader,
        ResumeLibrary resumes,
        NaukriJobBoard jobs,
        JobStore store,
        EvaluationStore evaluations,
        NaukriFetchRunner fetch,
        SearchLocationStore searches)
    {
        _matcher = matcher;
        _resumeReader = resumeReader;
        _resumes = resumes;
        _jobs = jobs;
        _store = store;
        _evaluations = evaluations;
        _fetch = fetch;
        _searches = searches;
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
    public string? Keywords { get; set; }

    [BindProperty]
    public string? Location { get; set; }

    [BindProperty]
    public string? EvaluateUrl { get; set; }

    [BindProperty]
    public List<string> SelectedJobUrls { get; set; } = [];

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

    public IReadOnlyList<string> Places { get; private set; } = [];

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
        LoadJobs();
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
        LoadJobs();
        return Page();
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

    public IActionResult OnGetFetchStatus()
    {
        return new JsonResult(_fetch.Snapshot());
    }

    public async Task<IActionResult> OnPostFetchAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        if (!await TryReadResumeAsync(cancellationToken))
        {
            return Page();
        }

        if (string.IsNullOrWhiteSpace(Keywords))
        {
            ErrorMessage = "Add search keywords before fetching Naukri jobs.";
            LoadJobs();
            return Page();
        }

        _searches.Remember(UserId, "Naukri", Location);

        var langsmithKey = AgentEnvFile.LangSmithKey(HttpContext.Session.GetString("LangSmithApiKey"));
        if (!_fetch.TryStart(new JobFetchRequest(
            Keywords.Trim(),
            Location?.Trim() ?? "",
            apiKey,
            ResumeText ?? "",
            ShowWeakMatches,
            PostedFrom,
            PostedTo,
            string.IsNullOrWhiteSpace(Place) ? null : Place.Trim(),
            PageNumber,
            SignedInUser.Id(User),
            langsmithKey)))
        {
            ErrorMessage = "A Naukri fetch is already running. You can keep using the list while it finishes.";
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

        var job = string.IsNullOrWhiteSpace(EvaluateUrl) ? null : _store.Find(UserId, EvaluateUrl);
        if (job is null)
        {
            ErrorMessage = "Choose a job to evaluate.";
            LoadJobs();
            return Page();
        }

        var (scored, missed, note) = await EvaluateJobsAsync([job], apiKey, cancellationToken);
        if (scored == 1 && missed == 0)
        {
            Message = $"{job.Title} scored {_store.Find(UserId, job.JobUrl)?.Score}. The score is in the Score column.";
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
        Message = missed == 0
            ? $"Evaluated {scored} selected jobs. The scores are in the Score column."
            : null;
        if (missed > 0)
        {
            ErrorMessage = $"Evaluated {scored} selected jobs. {missed} could not be scored. {note}";
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
                if (description.Length < 80)
                {
                    _store.Save(UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], "The job description could not be read."));
                    missed++;
                    note ??= "The job description could not be read.";
                    continue;
                }

                var report = await _matcher.MatchAsync(ResumeText!, description, job.Title, cancellationToken, apiKey, AgentEnvFile.LangSmithKey(HttpContext.Session.GetString("LangSmithApiKey")));
                _store.Save(UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, report.Score, report.Summary, report.Matched, report.Missing, null));
                _evaluations.Save(UserId, new JobEvaluation(job.JobUrl, job.Title, job.Company, job.Location, description, report.Score, report.Summary, report.Matched, report.Missing));
                scored++;
            }
            catch (InvalidOperationException exception)
            {
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
                note = "Naukri took too long on one job. Scores already stored are still in the table.";
                break;
            }
        }

        return (scored, missed, note);
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

    private bool HasKey()
    {
        var saved = HttpContext.Session.GetString(ApiKeySessionKey);
        return !string.IsNullOrWhiteSpace(saved) && saved.StartsWith("sk-", StringComparison.Ordinal);
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

        return "/Naukri?" + string.Join("&", query);
    }

    private void LoadJobs()
    {
        if (PostedFrom is not null && PostedTo is not null && PostedFrom > PostedTo)
        {
            (PostedFrom, PostedTo) = (PostedTo, PostedFrom);
        }

        Place = string.IsNullOrWhiteSpace(Place) ? null : Place.Trim();
        var saved = _store.ListBySource(UserId, "Naukri");
        Places = _searches.List(UserId, "Naukri", Place);
        var jobs = JobListQuery.Apply(saved, ShowWeakMatches, PostedFrom, PostedTo, Place, PageNumber, out var total, out var pageCount, out var pageNumber);
        var scores = _evaluations.ByUrl(UserId);
        Jobs = jobs.Select(job => job.Score is not null || !scores.TryGetValue(job.JobUrl, out var evaluation)
            ? job
            : job with
            {
                Score = evaluation.Score,
                Summary = evaluation.Summary,
                Covered = evaluation.Covered,
                Missing = evaluation.Missing,
                Error = null
            }).ToList();
        TotalJobs = total;
        PageCount = pageCount;
        PageNumber = pageNumber;
        Resumes = _resumes.List(UserId);
        var fetch = _fetch.Snapshot();
        FetchRunning = fetch.Running;
        FetchFailed = fetch.Failed;
        FetchMessage = string.IsNullOrWhiteSpace(fetch.Message) ? null : fetch.Message;
    }
}
