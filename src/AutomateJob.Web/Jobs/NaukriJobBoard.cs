using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace AutomateJob.Web.Jobs;

public sealed class NaukriJobBoard : IAsyncDisposable
{
    private const int PageSize = 20;
    private const int MaxResults = 1000;
    private static readonly Regex DayPattern = new(@"(\d+)\+?\s*day", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _profileDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private IPage? _page;

    public NaukriJobBoard(IWebHostEnvironment environment)
    {
        _profileDirectory = Path.Combine(AppHost.DataRoot(environment.ContentRootPath), "naukri-browser");
    }

    public async Task<IReadOnlyList<LinkedInJob>> SearchAsync(
        string keywords,
        string location,
        CancellationToken cancellationToken,
        Action<LinkedInJob>? onListed = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsurePageAsync();
            var jobs = new List<LinkedInJob>();
            for (var number = 1; jobs.Count < MaxResults; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (number > 1)
                {
                    await page.WaitForTimeoutAsync(700);
                }

                await page.GotoAsync(SearchUrl(keywords, location, number), new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 45_000
                });
                if (page.Url.Contains("access", StringComparison.OrdinalIgnoreCase)
                    || (await page.TitleAsync()).Contains("Access Denied", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Naukri blocked the job list. Leave the Naukri window open and fetch again.");
                }

                try
                {
                    await page.WaitForSelectorAsync(".srp-jobtuple-wrapper", new PageWaitForSelectorOptions { Timeout = 20_000 });
                }
                catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
                {
                    if (jobs.Count == 0)
                    {
                        throw new InvalidOperationException("Naukri returned no jobs for that search. Change the keywords or location and try again.");
                    }

                    break;
                }

                var parsed = ParseCards(await page.EvaluateAsync<string>(ReadCardsScript));
                var added = 0;
                foreach (var job in parsed)
                {
                    if (jobs.Any(item => item.Url == job.Url))
                    {
                        continue;
                    }

                    jobs.Add(job);
                    added++;
                    onListed?.Invoke(job);
                }

                if (parsed.Count < PageSize || added == 0)
                {
                    break;
                }
            }

            if (jobs.Count == 0)
            {
                throw new InvalidOperationException("Naukri returned no jobs for that search. Change the keywords or location and try again.");
            }

            return jobs;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> ReadDescriptionAsync(string jobUrl, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsurePageAsync();
            await page.GotoAsync(jobUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45_000
            });
            try
            {
                await page.WaitForSelectorAsync(".styles_JDC__dang-inner-html, .job-desc, [class*='dang-inner']", new PageWaitForSelectorOptions { Timeout = 12_000 });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
            }

            return await page.EvaluateAsync<string>(ReadDescriptionScript) ?? "";
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string SearchUrl(string keywords, string location, int page)
    {
        var key = Slug(keywords);
        var place = Slug(location);
        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("Add search keywords before fetching Naukri jobs.");
        }

        var path = string.IsNullOrEmpty(place) ? key + "-jobs" : key + "-jobs-in-" + place;
        if (page > 1)
        {
            path += "-" + page;
        }

        return "https://www.naukri.com/" + path;
    }

    internal static string PostedOn(string posted, DateOnly today)
    {
        var text = posted.Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return "";
        }

        if (text is "today" or "just now" || text.Contains("hour", StringComparison.Ordinal) || text.Contains("minute", StringComparison.Ordinal))
        {
            return today.ToString("yyyy-MM-dd");
        }

        var days = DayPattern.Match(text);
        if (days.Success && int.TryParse(days.Groups[1].Value, out var count))
        {
            return today.AddDays(-count).ToString("yyyy-MM-dd");
        }

        return "";
    }

    internal static IReadOnlyList<LinkedInJob> ParseCards(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var cards = JsonSerializer.Deserialize<List<NaukriCard>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        var today = DateOnly.FromDateTime(DateTime.Today);
        var jobs = new List<LinkedInJob>();
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Title) || string.IsNullOrWhiteSpace(card.Url))
            {
                continue;
            }

            var posted = (card.Posted ?? "").Trim();
            jobs.Add(new LinkedInJob(
                card.Url.Trim(),
                card.Title.Trim(),
                string.IsNullOrWhiteSpace(card.Company) ? "Unknown company" : card.Company.Trim(),
                (card.Location ?? "").Trim(),
                "",
                posted,
                PostedOn(posted, today)));
        }

        return jobs;
    }

    private async Task<IPage> EnsurePageAsync()
    {
        if (_page is not null && !_page.IsClosed)
        {
            return _page;
        }

        _playwright ??= await Playwright.CreateAsync();
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = AppHost.Headless(),
            ViewportSize = ViewportSize.NoViewport
        };
        var chrome = Environment.GetEnvironmentVariable("CHROME_PATH");
        if (!string.IsNullOrWhiteSpace(chrome))
        {
            options.ExecutablePath = chrome.Trim();
        }

        options.Args = AppHost.Headless()
            ? ["--no-sandbox", "--disable-dev-shm-usage"]
            : ["--start-maximized"];
        _context = await _playwright.Chromium.LaunchPersistentContextAsync(_profileDirectory, options);
        _page = _context.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
        return _page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync();
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }

    private static string Slug(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        text = Regex.Replace(text, @"[^a-z0-9]+", "-");
        return text.Trim('-');
    }

    private const string ReadCardsScript = """
        () => JSON.stringify([...document.querySelectorAll('.srp-jobtuple-wrapper')].map(card => ({
            url: card.querySelector('a.title')?.href || '',
            title: (card.querySelector('a.title')?.innerText || '').replace(/\s+/g, ' ').trim(),
            company: (card.querySelector('.comp-name')?.innerText || '').replace(/\s+/g, ' ').trim(),
            location: (card.querySelector('.locWdth')?.innerText || '').replace(/\s+/g, ' ').trim(),
            posted: (card.querySelector('.job-post-day')?.innerText || '').replace(/\s+/g, ' ').trim()
        })).filter(card => card.url && card.title))
        """;

    private const string ReadDescriptionScript = """
        () => {
            const node = document.querySelector(".styles_JDC__dang-inner-html, .job-desc, [class*='dang-inner']");
            return ((node && node.innerText) || '').replace(/[ \t]+\n/g, '\n').trim();
        }
        """;

    private sealed class NaukriCard
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Location { get; set; } = "";
        public string Posted { get; set; } = "";
    }
}
