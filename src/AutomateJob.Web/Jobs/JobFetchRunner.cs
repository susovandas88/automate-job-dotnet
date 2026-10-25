using AutomateJob.Web.Matching;
using Microsoft.Extensions.DependencyInjection;

namespace AutomateJob.Web.Jobs;

public sealed record JobFetchRequest(
    string Keywords,
    string Location,
    string? ApiKey,
    string ResumeText,
    bool ShowWeakMatches,
    DateOnly? PostedFrom,
    DateOnly? PostedTo,
    string? Place,
    int PageNumber,
    long UserId,
    string? LangSmithApiKey = null);

public sealed record JobFetchSnapshot(bool Running, bool Failed, string Message, int Saved);

public sealed class JobFetchRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly object _gate = new();
    private bool _running;
    private bool _failed;
    private string _message = "";
    private int _saved;
    private CancellationTokenSource? _cancel;

    public JobFetchRunner(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime)
    {
        _scopes = scopes;
        _lifetime = lifetime;
    }

    public JobFetchSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new JobFetchSnapshot(_running, _failed, _message, _saved);
        }
    }

    public bool TryStart(JobFetchRequest request)
    {
        lock (_gate)
        {
            if (_running)
            {
                return false;
            }

            _cancel?.Dispose();
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
            _running = true;
            _failed = false;
            _saved = 0;
            _message = "Fetching jobs from LinkedIn. You can keep using the application.";
        }

        _ = RunAsync(request);
        return true;
    }

    public void Cancel()
    {
        CancellationTokenSource? source;
        lock (_gate)
        {
            source = _cancel;
            if (source is null)
            {
                return;
            }

            _message = "Cancelling the LinkedIn fetch. Jobs already saved stay in the list.";
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(JobFetchRequest request)
    {
        CancellationToken token;
        lock (_gate)
        {
            token = _cancel?.Token ?? _lifetime.ApplicationStopping;
        }
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var board = scope.ServiceProvider.GetRequiredService<LinkedInJobBoard>();
            var store = scope.ServiceProvider.GetRequiredService<JobStore>();
            var evaluations = scope.ServiceProvider.GetRequiredService<EvaluationStore>();
            var matcher = scope.ServiceProvider.GetRequiredService<LlmJobMatcher>();

            var (scored, relevant, missed) = await ScoreVisibleAsync(request, board, store, evaluations, matcher, token);
            var found = await board.SearchAsync(request.Keywords, request.Location, token, job =>
            {
                store.SaveListing(request.UserId, job);
                lock (_gate)
                {
                    _saved++;
                    _message = $"Fetching jobs from LinkedIn. {_saved} saved so far. You can keep using the application.";
                }
            });

            var listed = await ScoreVisibleAsync(request, board, store, evaluations, matcher, token);
            scored += listed.Scored;
            relevant += listed.Relevant;
            missed += listed.Missed;

            var cap = found.Count >= 1000 ? " LinkedIn's public search stops at 1,000 jobs." : "";
            var message = !AutomateJob.Web.Matching.LlmRoute.CanScore(request.ApiKey)
                ? AutomateJob.Web.Matching.LlmRoute.OpenAiEnabled()
                    ? $"Listed {found.Count} jobs. The Score column stays blank until you paste an OpenAI key that starts with sk-.{cap}"
                    : $"Listed {found.Count} jobs. OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in the web .env file, then click Score this page.{cap}"
                : missed == 0
                    ? $"Listed {found.Count} jobs. The Score column now shows {scored} scores. {relevant} are relevant to the resume.{cap}"
                    : $"Listed {found.Count} jobs. {missed} jobs on this page could not be scored.{cap}";
            lock (_gate)
            {
                _failed = false;
                _message = message;
            }
        }
        catch (InvalidOperationException exception)
        {
            Fail(exception.Message);
        }
        catch (HttpRequestException)
        {
            Fail("OpenAI could not be reached. Jobs already scored are still in the table.");
        }
        catch (TimeoutException)
        {
            Fail("LinkedIn took too long on one job. Jobs already scored are still in the table. Fetch again.");
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _failed = false;
                _message = "Fetch cancelled. Jobs already saved stay in the list.";
            }
        }
        catch (Exception)
        {
            Fail("The LinkedIn fetch stopped. Jobs already saved are still in the table.");
        }
        finally
        {
            lock (_gate)
            {
                _running = false;
                _cancel?.Dispose();
                _cancel = null;
            }
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _failed = true;
            _message = message;
        }
    }

    private static async Task<(int Scored, int Relevant, int Missed)> ScoreVisibleAsync(
        JobFetchRequest request,
        LinkedInJobBoard board,
        JobStore store,
        EvaluationStore evaluations,
        LlmJobMatcher matcher,
        CancellationToken cancellationToken)
    {
        if (!AutomateJob.Web.Matching.LlmRoute.CanScore(request.ApiKey) || string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return (0, 0, 0);
        }

        var scores = evaluations.ByUrl(request.UserId);
        var page = JobListQuery.Apply(
            store.ListBySource(request.UserId, "LinkedIn"),
            request.ShowWeakMatches,
            request.PostedFrom,
            request.PostedTo,
            request.Place,
            request.PageNumber,
            out _,
            out _,
            out _);
        var scored = 0;
        var relevant = 0;
        var missed = 0;
        foreach (var job in page)
        {
            var current = job.Score is not null || !scores.TryGetValue(job.JobUrl, out var evaluation)
                ? job
                : job with
                {
                    Score = evaluation.Score,
                    Summary = evaluation.Summary,
                    Covered = evaluation.Covered,
                    Missing = evaluation.Missing,
                    Error = null
                };
            if (current.Score is not null && string.IsNullOrWhiteSpace(current.Error))
            {
                continue;
            }

            if (!current.JobUrl.Contains("/jobs/view/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var description = await board.ReadDescriptionAsync(current.JobUrl, cancellationToken);
                var stored = await ScoreAsync(request, matcher, new LinkedInJob(current.JobUrl, current.Title, current.Company, current.Location, description, current.Posted, ""), cancellationToken);
                store.Save(request.UserId, stored);
                if (stored.Score is int score)
                {
                    evaluations.Save(request.UserId, new JobEvaluation(
                        current.JobUrl,
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
                store.Save(request.UserId, new StoredJob(0, current.JobUrl, current.Title, current.Company, current.Location, current.Posted, null, null, [], [], exception.Message));
                missed++;
            }
        }

        return (scored, relevant, missed);
    }

    private static async Task<StoredJob> ScoreAsync(
        JobFetchRequest request,
        LlmJobMatcher matcher,
        LinkedInJob job,
        CancellationToken cancellationToken)
    {
        if (job.Description.Length < 80)
        {
            return new StoredJob(0, job.Url, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], "The job description could not be read.");
        }

        try
        {
            var report = await matcher.MatchAsync(request.ResumeText, job.Description, job.Title, cancellationToken, request.ApiKey, request.LangSmithApiKey);
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
}
