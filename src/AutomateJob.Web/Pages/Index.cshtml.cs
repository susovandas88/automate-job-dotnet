using AutomateJob.Web.Auth;
using AutomateJob.Web.Jobs;
using AutomateJob.Web.Matching;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class IndexModel : PageModel
{
    private const string ApiKeySessionKey = "OpenAiApiKey";

    private readonly LlmJobMatcher _matcher;
    private readonly ResumeTextReader _resumeReader;
    private readonly ResumeLibrary _resumes;
    private readonly EvaluationStore _evaluations;

    private long UserId => SignedInUser.Id(User);

    public IndexModel(LlmJobMatcher matcher, ResumeTextReader resumeReader, ResumeLibrary resumes, EvaluationStore evaluations)
    {
        _matcher = matcher;
        _resumeReader = resumeReader;
        _resumes = resumes;
        _evaluations = evaluations;
    }

    [BindProperty]
    public IFormFile? ResumeFile { get; set; }

    [BindProperty]
    public string? ResumeText { get; set; }

    [BindProperty]
    public string? SelectedResumeId { get; set; }

    public IReadOnlyList<StoredResume> Resumes { get; private set; } = [];

    [BindProperty]
    public string? JobTitle { get; set; }

    [BindProperty]
    public string? JobDescription { get; set; }

    [BindProperty]
    public string? ApiKey { get; set; }

    public MatchReport? Report { get; private set; }

    public string? Message { get; private set; }

    public string? ErrorMessage { get; private set; }

    public bool HasSavedKey { get; private set; }

    public void OnGet()
    {
        HasSavedKey = !string.IsNullOrWhiteSpace(HttpContext.Session.GetString(ApiKeySessionKey));
        LoadResumes();
    }

    public async Task<IActionResult> OnPostLoadResumeAsync(CancellationToken cancellationToken)
    {
        ResolvePostedApiKey();
        try
        {
            await UsePostedResumeAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception)
        {
            ErrorMessage = "The resume file could not be read. Use a text-based PDF, DOCX, or TXT file.";
        }

        LoadResumes();
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

        LoadResumes();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var apiKey = ResolvePostedApiKey();
        try
        {
            await UsePostedResumeAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = exception.Message;
            LoadResumes();
            return Page();
        }
        catch (Exception)
        {
            ErrorMessage = "The resume file could not be read. Use a text-based PDF, DOCX, or TXT file.";
            LoadResumes();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(ResumeText))
        {
            ErrorMessage = "Add your resume by uploading a file or pasting the text.";
            LoadResumes();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(JobDescription) || JobDescription.Trim().Length < 40)
        {
            ErrorMessage = "Paste a job description with the role requirements.";
            LoadResumes();
            return Page();
        }

        try
        {
            Report = await _matcher.MatchAsync(ResumeText, JobDescription, JobTitle, cancellationToken, apiKey, AgentEnvFile.LangSmithKey(HttpContext.Session.GetString("LangSmithApiKey")));
            _evaluations.Save(SignedInUser.Id(User), new JobEvaluation(
                EvaluationStore.KeyForPastedDescription(JobDescription),
                string.IsNullOrWhiteSpace(JobTitle) ? "Pasted job" : JobTitle.Trim(),
                "",
                "",
                JobDescription.Trim(),
                Report.Score,
                Report.Summary,
                Report.Matched,
                Report.Missing));
        }
        catch (InvalidOperationException exception)
        {
            if (exception.Message.Contains("API key", StringComparison.OrdinalIgnoreCase))
            {
                HttpContext.Session.Remove(ApiKeySessionKey);
                HasSavedKey = false;
            }

            ErrorMessage = exception.Message;
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "The model took too long to score this match. Try again.";
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "The model could not be reached. Check the network and try again.";
        }

        LoadResumes();
        return Page();
    }

    private async Task UsePostedResumeAsync(CancellationToken cancellationToken)
    {
        if (ResumeFile is { Length: > 0 })
        {
            ResumeText = await _resumeReader.ReadAsync(ResumeFile, cancellationToken);
            SelectedResumeId = _resumes.Save(UserId, ResumeFile.FileName, ResumeText);
            return;
        }

        if (!string.IsNullOrWhiteSpace(SelectedResumeId))
        {
            ResumeText = _resumes.Read(UserId, SelectedResumeId) ?? throw new InvalidOperationException("That stored resume could not be found.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(ResumeText))
        {
            SelectedResumeId = _resumes.Save(UserId, ResumeLibrary.NameFromText(ResumeText), ResumeText);
        }
    }

    private void LoadResumes()
    {
        Resumes = _resumes.List(UserId);
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
}
