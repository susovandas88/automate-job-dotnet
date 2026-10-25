using AutomateJob.Web.Matching;

namespace AutomateJob.Web.Jobs;

public sealed class NaukriFetchRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly object _gate = new();
    private bool _running;
    private bool _failed;
    private string _message = "";
    private int _saved;
    private CancellationTokenSource? _cancel;

    public NaukriFetchRunner(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime)
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
            _message = "Fetching jobs from Naukri. A browser window stays open. You can keep using the application.";
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

            _message = "Cancelling the Naukri fetch. Jobs already saved stay in the list.";
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
            var board = scope.ServiceProvider.GetRequiredService<NaukriJobBoard>();
            var store = scope.ServiceProvider.GetRequiredService<JobStore>();
            var evaluations = scope.ServiceProvider.GetRequiredService<EvaluationStore>();
            var matcher = scope.ServiceProvider.GetRequiredService<LlmJobMatcher>();

            var found = await board.SearchAsync(request.Keywords, request.Location, token, job =>
            {
                store.SaveListing(request.UserId, job, "Naukri");
                lock (_gate)
                {
                    _saved++;
                    _message = $"Fetching jobs from Naukri. {_saved} saved so far. You can keep using the application.";
                }
            });

            var (scored, missed) = await ScoreVisibleAsync(request, board, store, evaluations, matcher, token);
            var cap = found.Count >= 1000 ? " The list stops at 1,000 jobs." : "";
            var message = !AutomateJob.Web.Matching.LlmRoute.CanScore(request.ApiKey)
                ? AutomateJob.Web.Matching.LlmRoute.OpenAiEnabled()
                    ? $"Listed {found.Count} Naukri jobs. The Score column stays blank until you paste an OpenAI key that starts with sk-.{cap}"
                    : $"Listed {found.Count} Naukri jobs. OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in the web .env file, then click Evaluate selected.{cap}"
                : missed == 0
                    ? $"Listed {found.Count} Naukri jobs. The Score column now shows {scored} scores on this page.{cap}"
                    : $"Listed {found.Count} Naukri jobs. {missed} jobs on this page could not be scored.{cap}";
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
            Fail("OpenAI could not be reached. Jobs already saved are still in the table.");
        }
        catch (TimeoutException)
        {
            Fail("Naukri took too long on one page. Jobs already saved are still in the table. Fetch again.");
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
            Fail("The Naukri fetch stopped. Jobs already saved are still in the table.");
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

    private static async Task<(int Scored, int Missed)> ScoreVisibleAsync(
        JobFetchRequest request,
        NaukriJobBoard board,
        JobStore store,
        EvaluationStore evaluations,
        LlmJobMatcher matcher,
        CancellationToken cancellationToken)
    {
        if (!AutomateJob.Web.Matching.LlmRoute.CanScore(request.ApiKey) || string.IsNullOrWhiteSpace(request.ResumeText))
        {
            return (0, 0);
        }

        var page = JobListQuery.Apply(
            store.ListBySource(request.UserId, "Naukri"),
            request.ShowWeakMatches,
            request.PostedFrom,
            request.PostedTo,
            request.Place,
            request.PageNumber,
            out _,
            out _,
            out _);
        var scored = 0;
        var missed = 0;
        foreach (var job in page)
        {
            if (job.Score is not null && string.IsNullOrWhiteSpace(job.Error))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var description = await board.ReadDescriptionAsync(job.JobUrl, cancellationToken);
                if (description.Length < 80)
                {
                    store.Save(request.UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], "The job description could not be read."));
                    missed++;
                    continue;
                }

                var report = await matcher.MatchAsync(request.ResumeText, description, job.Title, cancellationToken, request.ApiKey, request.LangSmithApiKey);
                store.Save(request.UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, report.Score, report.Summary, report.Matched, report.Missing, null));
                evaluations.Save(request.UserId, new JobEvaluation(job.JobUrl, job.Title, job.Company, job.Location, description, report.Score, report.Summary, report.Matched, report.Missing));
                scored++;
            }
            catch (InvalidOperationException exception)
            {
                store.Save(request.UserId, new StoredJob(0, job.JobUrl, job.Title, job.Company, job.Location, job.Posted, null, null, [], [], exception.Message));
                missed++;
            }
        }

        return (scored, missed);
    }
}
