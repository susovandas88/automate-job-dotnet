using System.Diagnostics;
using System.Text.Json;
using AutomateJob.Web.Matching;
using Microsoft.Playwright;

namespace AutomateJob.Web.Jobs;

public sealed class LinkedInSession : IAsyncDisposable
{
    private const int MaxJobs = 25;

    private readonly string _profileDirectory;
    private readonly string _chromeProfile;
    private readonly LangGraphApplyAgent _agent;
    private readonly ApplicationPace _pace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;
    private bool _attachedChrome;

    public LinkedInSession(IWebHostEnvironment environment, LangGraphApplyAgent agent)
    {
        var root = AppHost.DataRoot(environment.ContentRootPath);
        _profileDirectory = Path.Combine(root, "linkedin-browser");
        _chromeProfile = AppHost.ChromeProfile(environment.ContentRootPath);
        _pace = new ApplicationPace(Path.Combine(root, "pace.json"));
        _agent = agent;
    }

    public bool IsOpen => _page is not null;

    public async Task OpenLoginAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsurePageAsync();
            await page.GotoAsync("https://www.linkedin.com/login", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(BrowserMessage(exception), exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<LinkedInJob>> FetchAsync(
        string keywords,
        string location,
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxJobs);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var page = await EnsurePageAsync();
            var search = "https://www.linkedin.com/jobs/search/?keywords="
                + Uri.EscapeDataString(keywords)
                + "&location="
                + Uri.EscapeDataString(location);
            await page.GotoAsync(search, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            EnsureSignedIn(page);
            try
            {
                await page.WaitForSelectorAsync("a[href*='/jobs/view/']", new PageWaitForSelectorOptions
                {
                    Timeout = 20_000
                });
            }
            catch (PlaywrightException)
            {
                throw new InvalidOperationException(
                    "LinkedIn did not show a job list. Sign in on the LinkedIn window, then fetch again.");
            }

            var cards = new List<JobCard>();
            for (var attempt = 0; attempt < 8 && cards.Count < limit; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                cards = await ReadCardsAsync(page, limit);
                await ScrollJobListAsync(page);
                await page.WaitForTimeoutAsync(900);
            }

            if (cards.Count == 0)
            {
                throw new InvalidOperationException(
                    "No jobs were found. Sign in if the LinkedIn window is still on the login page, or change the search.");
            }

            var jobs = new List<LinkedInJob>();
            foreach (var card in cards.Take(limit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                jobs.Add(await ReadOpenJobAsync(page, card));
            }

            return jobs;
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            throw new InvalidOperationException(BrowserMessage(exception), exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ApplyOutcome> ApplyAsync(
        string jobUrl,
        string resume,
        ApplicantProfile profile,
        string apiKey,
        string? langsmithApiKey,
        IReadOnlyList<SavedAnswer> answers,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var blocked = _pace.BlockReason();
            if (blocked is not null)
            {
                return new ApplyOutcome("Needs you", [], [], blocked);
            }

            var gap = _pace.GapBeforeNext();
            if (gap > TimeSpan.Zero)
            {
                await Task.Delay(gap, cancellationToken);
            }

            _pace.MarkStarted();
            var page = await EnsurePageAsync();
            await page.GotoAsync(jobUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60_000
            });
            if (IsSignInPage(page.Url))
            {
                throw new InvalidOperationException(
                    "Sign in on the LinkedIn window, then click Apply again. The password stays on LinkedIn's page.");
            }

            var slot = new PageSlot { Page = page };
            var outcome = await _agent.RunAsync(
                (_, token) => ReadAgentPageForApplyAsync(slot.Page, false, token),
                (answers, _) => FillAnswersAsync(slot.Page, answers),
                token => ClickAdvanceAsync(slot.Page, token),
                _ => ClickCompanyApplyAsync(slot.Page),
                (call, token) => PerformFormToolAsync(slot.Page, call, token),
                async token =>
                {
                    var opened = await TryOpenApplicationFormAsync(slot.Page, token);
                    if (opened is not null && TabOpen(opened))
                    {
                        slot.Page = opened;
                        return (true, false);
                    }

                    var waiting = TabOpen(slot.Page) && await GateWaitingAsync(slot.Page);
                    return (false, waiting);
                },
                async (attempt, token) =>
                {
                    await page.GotoAsync(jobUrl, new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 60_000
                    });
                    var opened = await OpenAttemptAsync(page, Math.Max(0, attempt - 1), token);
                    if (opened is null || !TabOpen(opened))
                    {
                        return false;
                    }

                    slot.Page = opened;
                    await PrepareCompanyPageAsync(slot.Page);
                    return true;
                },
                _ => Task.FromResult(TabOpen(slot.Page)),
                token => WaitForHumanCaptchaAsync(slot.Page, token),
                resume,
                profile,
                apiKey,
                langsmithApiKey,
                answers,
                cancellationToken);
            var hint = "";
            try
            {
                if (TabOpen(slot.Page))
                {
                    hint = ApplicationRoute.Hint(slot.Page.Url);
                }
            }
            catch (PlaywrightException)
            {
            }

            return outcome with { Note = (hint + " " + outcome.Note).Trim() };
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            throw new InvalidOperationException(BrowserMessage(exception), exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class PageSlot
    {
        public IPage Page { get; set; } = null!;
    }

    private async Task<IPage?> OpenAttemptAsync(IPage page, int attempt, CancellationToken cancellationToken)
    {
        var company = await OpenCompanyFormAsync(page, cancellationToken);
        if (company is not null)
        {
            return company;
        }

        if (await OpenEasyApplyAsync(page))
        {
            return page;
        }

        return null;
    }

    private async Task<IPage?> TryOpenApplicationFormAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TabOpen(page))
        {
            return null;
        }

        var beforeUrl = page.Url;
        var beforeButtons = string.Join("|", await ReadButtonsAsync(page));
        string clicked;
        try
        {
            clicked = await page.EvaluateAsync<string>(ClickFormGateScript) ?? "";
        }
        catch (PlaywrightException)
        {
            return null;
        }

        if (clicked.Length == 0)
        {
            return null;
        }

        IPage? popup = null;
        if (clicked == "button")
        {
            try
            {
                popup = await page.Context.WaitForPageAsync(new BrowserContextWaitForPageOptions { Timeout = 8_000 });
            }
            catch (TimeoutException)
            {
            }
        }

        var target = popup is not null && !popup.IsClosed ? popup : page;
        try
        {
            await target.WaitForSelectorAsync(
                "input, textarea, select, [role='combobox'], [aria-haspopup='listbox']",
                new PageWaitForSelectorOptions { Timeout = clicked == "button" ? 15_000 : 4_000 });
        }
        catch (TimeoutException)
        {
        }

        if (popup is not null && !popup.IsClosed)
        {
            await popup.BringToFrontAsync();
            return popup;
        }

        var afterButtons = string.Join("|", await ReadButtonsAsync(page));
        if (!string.Equals(beforeUrl, page.Url, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(beforeButtons, afterButtons, StringComparison.Ordinal))
        {
            return page;
        }

        return null;
    }

    private static async Task<bool> GateWaitingAsync(IPage page)
    {
        if (!TabOpen(page))
        {
            return false;
        }

        try
        {
            return await page.EvaluateAsync<bool>(GateWaitingScript);
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static bool TabOpen(IPage page)
    {
        try
        {
            return !page.IsClosed;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task<bool> OpenEasyApplyAsync(IPage page)
    {
        var button = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            NameRegex = new System.Text.RegularExpressions.Regex(@"Easy Apply", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        });
        if (await button.CountAsync() == 0)
        {
            button = page.Locator("button.jobs-apply-button, a.jobs-apply-button").Filter(new LocatorFilterOptions
            {
                HasText = "Easy Apply"
            });
        }

        if (await button.CountAsync() == 0)
        {
            return false;
        }

        await button.First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        await TurnOffProfileShareAsync(page);
        var modal = page.Locator(".jobs-easy-apply-modal, .jobs-easy-apply-content");
        try
        {
            await modal.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            if (await page.GetByRole(AriaRole.Dialog).CountAsync() == 0)
            {
                return false;
            }
        }

        await page.BringToFrontAsync();
        return true;
    }

    private async Task<IPage?> OpenCompanyFormAsync(IPage page, CancellationToken cancellationToken)
    {
            var target = await FindOffsiteApplyAsync(page);
            if (target is null)
            {
                return null;
            }

            var before = page.Context.Pages.Where(candidate => !candidate.IsClosed).ToDictionary(candidate => candidate, candidate => candidate.Url);
            var known = page.Context.Pages.ToArray();
        var opened = new List<IPage>();
        void Remember(object? _, IPage next)
        {
            opened.Add(next);
        }

        page.Context.Page += Remember;
        page.Popup += Remember;
        try
        {
            await target.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
            await TurnOffProfileShareAsync(page);
            var continueLink = await ClickContinueAsync(page);
            var company = await WaitForCompanyTabAsync(page, known, opened, before, continueLink, cancellationToken);
            if (company is null || company.IsClosed)
            {
                return null;
            }

            await FollowCompanyPageAsync(company, cancellationToken);
            if (!IsCompanyForm(company.Url))
            {
                return null;
            }

            try
            {
                await company.WaitForSelectorAsync("input, textarea, select", new PageWaitForSelectorOptions { Timeout = 20_000 });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
            }

            await company.BringToFrontAsync();
            return company;
        }
        finally
        {
            page.Context.Page -= Remember;
            page.Popup -= Remember;
        }
    }

    private static async Task<ILocator?> FindOffsiteApplyAsync(IPage page)
    {
        var groups = new[]
        {
            page.Locator("a.jobs-apply-button, button.jobs-apply-button"),
            page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex(@"\bApply\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }),
            page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = new System.Text.RegularExpressions.Regex(@"\bApply\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })
        };

        foreach (var group in groups)
        {
            var count = await group.CountAsync();
            for (var index = 0; index < count; index++)
            {
                var item = group.Nth(index);
                if (!await IsVisibleAsync(item))
                {
                    continue;
                }

                var label = ((await item.GetAttributeAsync("aria-label")) + " " + await SafeTextAsync(item)).Trim();
                if (label.Contains("Easy Apply", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (label.Contains("Apply", StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }
        }

        return null;
    }

    private static async Task<bool> IsVisibleAsync(ILocator item)
    {
        try
        {
            return await item.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static async Task TurnOffProfileShareAsync(IPage page)
    {
        try
        {
            await page.GetByText("Increase your chance of being viewed").WaitForAsync(new LocatorWaitForOptions { Timeout = 4_000 });
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            return;
        }

        var share = page.Locator("[role='dialog'], .artdeco-modal").Filter(new LocatorFilterOptions
        {
            HasText = "Increase your chance of being viewed"
        });
        if (await share.CountAsync() == 0)
        {
            share = page.Locator("body");
        }

        var toggles = share.Locator("[role='switch'], input[type='checkbox'], button.artdeco-toggle__button");
        if (await toggles.CountAsync() == 0)
        {
            return;
        }

        var toggle = toggles.First;
        var pressed = await toggle.GetAttributeAsync("aria-checked");
        var on = string.Equals(pressed, "true", StringComparison.OrdinalIgnoreCase);
        if (!on)
        {
            try
            {
                on = await toggle.IsCheckedAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        if (!on && await share.GetByText("On", new LocatorGetByTextOptions { Exact = true }).CountAsync() > 0)
        {
            on = true;
        }

        if (on)
        {
            try
            {
                await toggle.ClickAsync(new LocatorClickOptions { Timeout = 3_000, Force = true });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
            }
        }
    }

    private static async Task<string?> ClickContinueAsync(IPage page)
    {
        var dialog = page.Locator("[role='dialog'], .artdeco-modal").Filter(new LocatorFilterOptions
        {
            HasText = "Share your profile"
        });
        if (await dialog.CountAsync() == 0)
        {
            dialog = page.Locator("[role='dialog'], .artdeco-modal").Filter(new LocatorFilterOptions
            {
                HasText = "Increase your chance of being viewed"
            });
        }

        var continueName = new System.Text.RegularExpressions.Regex(@"Continue", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        ILocator button = await dialog.CountAsync() > 0
            ? dialog.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { NameRegex = continueName })
            : page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameRegex = continueName });
        if (await button.CountAsync() == 0)
        {
            button = await dialog.CountAsync() > 0
                ? dialog.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { NameRegex = continueName })
                : page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { NameRegex = continueName });
        }

        if (await button.CountAsync() == 0)
        {
            return null;
        }

        var control = button.Last;
        var href = await control.GetAttributeAsync("href");
        await control.ClickAsync(new LocatorClickOptions { Timeout = 8_000, Force = true });
        return string.IsNullOrWhiteSpace(href) ? null : href;
    }

    private static async Task<IPage?> WaitForCompanyTabAsync(
        IPage page,
        IPage[] known,
        List<IPage> opened,
        Dictionary<IPage, string> before,
        string? continueLink,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(12))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var company = NewestCompanyPage(page, known, opened, before);
            if (company is not null)
            {
                return company;
            }

            await page.WaitForTimeoutAsync(400);
        }

        if (string.IsNullOrWhiteSpace(continueLink))
        {
        return NewestCompanyPage(page, known, opened, before);
    }

        var destination = continueLink.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? continueLink
            : "https://www.linkedin.com" + (continueLink.StartsWith('/') ? continueLink : "/" + continueLink);
        var companyPage = await page.Context.NewPageAsync();
        await companyPage.GotoAsync(destination, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 30_000
        });
        return companyPage;
    }

    private static IPage? NewestCompanyPage(IPage page, IPage[] known, List<IPage> opened, Dictionary<IPage, string> before)
    {
        var company = opened.LastOrDefault(candidate => !candidate.IsClosed)
            ?? page.Context.Pages.LastOrDefault(candidate => !known.Contains(candidate) && candidate != page && !candidate.IsClosed);
        if (company is not null)
        {
            return company;
        }

        if (IsCompanyForm(page.Url))
        {
            return page;
        }

        return page.Context.Pages.LastOrDefault(candidate =>
            !candidate.IsClosed
            && candidate != page
            && before.TryGetValue(candidate, out var previous)
            && !string.Equals(previous, candidate.Url, StringComparison.OrdinalIgnoreCase)
            && (IsCompanyForm(candidate.Url) || ExternalUrl(candidate.Url) is not null));
    }

    private static async Task FollowCompanyPageAsync(IPage company, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (!IsCompanyForm(company.Url) && DateTime.UtcNow - started < TimeSpan.FromSeconds(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var external = ExternalUrl(company.Url);
            if (external is not null)
            {
                await company.GotoAsync(external, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20_000
                });
                return;
            }

            await company.WaitForTimeoutAsync(500);
        }

        try
        {
            await company.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 15_000 });
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
        }
    }

    private static string? ExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in query)
        {
            var bits = part.Split('=', 2);
            if (bits.Length != 2 || (bits[0] != "url" && bits[0] != "session_redirect"))
            {
                continue;
            }

            var destination = Uri.UnescapeDataString(bits[1]);
            if (destination.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return destination;
            }
        }

        return null;
    }

    private static async Task<string> SafeTextAsync(ILocator item)
    {
        try
        {
            return await item.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 1_000 });
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            return "";
        }
    }

    private static bool IsCompanyForm(string url)
    {
        return !string.IsNullOrWhiteSpace(url)
            && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SignOutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseCoreAsync();
            if (Directory.Exists(_profileDirectory))
            {
                Directory.Delete(_profileDirectory, recursive: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseCoreAsync();
        _gate.Dispose();
    }

    private string ChromeLaunchMessage()
    {
        return "Chrome would not open a debugging port. Close every Chrome window, then start: \""
            + AppHost.ChromePath()
            + "\" "
            + AppHost.ChromeArguments(_chromeProfile);
    }

    private async Task EnsureChromeListeningAsync()
    {
        if (await DebugPortOpenAsync())
        {
            return;
        }

        var chrome = AppHost.ChromePath();
        if (!File.Exists(chrome))
        {
            throw new InvalidOperationException("Google Chrome was not found at " + chrome + ".");
        }

        Directory.CreateDirectory(_chromeProfile);
        Process.Start(new ProcessStartInfo
        {
            FileName = chrome,
            Arguments = AppHost.ChromeArguments(_chromeProfile),
            UseShellExecute = false
        });

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await DebugPortOpenAsync())
            {
                return;
            }

            await Task.Delay(500);
        }

        throw new InvalidOperationException(ChromeLaunchMessage());
    }

    private static async Task<bool> DebugPortOpenAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("http://127.0.0.1:9222/json/version");
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task<IPage> EnsurePageAsync()
    {
        if (_page is not null && !_page.IsClosed)
        {
            return _page;
        }

        _playwright ??= await Playwright.CreateAsync();
        await EnsureChromeListeningAsync();
        try
        {
            _browser = await _playwright.Chromium.ConnectOverCDPAsync("http://127.0.0.1:9222");
        }
        catch (PlaywrightException exception)
        {
            throw new InvalidOperationException(ChromeLaunchMessage(), exception);
        }

        _attachedChrome = true;
        _context = _browser.Contexts.FirstOrDefault() ?? await _browser.NewContextAsync();
        _page = _context.Pages.FirstOrDefault(page => !page.IsClosed) ?? await _context.NewPageAsync();
        return _page;
    }

    private static async Task<List<JobCard>> ReadCardsAsync(IPage page, int limit)
    {
        var cards = await page.EvaluateAsync<JobCard[]>(CardScript);
        var unique = new List<JobCard>();
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Id) || unique.Any(item => item.Id == card.Id))
            {
                continue;
            }

            unique.Add(card);
            if (unique.Count >= limit)
            {
                break;
            }
        }

        return unique;
    }

    private static async Task ScrollJobListAsync(IPage page)
    {
        var scrolled = await page.EvaluateAsync<bool>(
            """
            () => {
                const list = document.querySelector('.jobs-search-results-list, .scaffold-layout__list');
                if (!list) return false;
                list.scrollTop = list.scrollHeight;
                return true;
            }
            """);
        if (!scrolled)
        {
            await page.Mouse.WheelAsync(0, 1400);
        }
    }

    private async Task<LinkedInJob> ReadOpenJobAsync(IPage page, JobCard card)
    {
        var url = "https://www.linkedin.com/jobs/view/" + card.Id + "/";
        var link = page.Locator($"a[href*='/jobs/view/{card.Id}']").First;
        await link.ScrollIntoViewIfNeededAsync();
        await link.ClickAsync();
        EnsureSignedIn(page);

        var seeMore = page.Locator("button.jobs-description__footer-button, button[aria-label*='see more' i]");
        if (await seeMore.CountAsync() > 0)
        {
            try
            {
                await seeMore.First.ClickAsync(new LocatorClickOptions { Timeout = 2_000 });
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
            }
        }

        try
        {
            await page.WaitForFunctionAsync(
                "() => (document.querySelector('#job-details, .jobs-description__content, .jobs-box__html-content')?.innerText || '').trim().length > 80",
                null,
                new PageWaitForFunctionOptions { Timeout = 12_000 });
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
        }

        var detail = await page.EvaluateAsync<JobDetail>(DetailScript);
        var title = FirstFilled(LinkedInText.Title(detail.Title), LinkedInText.Title(card.Title));
        var company = FirstFilled(LinkedInText.Company(detail.Company), LinkedInText.Company(card.Company));
        var location = FirstFilled(LinkedInText.Location(detail.Location), LinkedInText.Location(card.Location));
        var description = detail.Description?.Trim() ?? "";

        return new LinkedInJob(
            url,
            string.IsNullOrWhiteSpace(title) ? "Untitled job" : title,
            string.IsNullOrWhiteSpace(company) ? "Unknown company" : company,
            location,
            description,
            "",
            "");
    }

    private static string FirstFilled(string preferred, string fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    private const string CardScript = """
        () => {
            const seen = new Set();
            const jobs = [];
            const links = document.querySelectorAll("a[href*='/jobs/view/']");
            for (const link of links) {
                const match = link.href.match(/\/jobs\/view\/(\d+)/);
                if (!match || seen.has(match[1])) continue;
                seen.add(match[1]);
                const card = link.closest('li, .job-card-container, .job-card-list') || link.parentElement;
                const text = (selector) => (card?.querySelector(selector)?.innerText || '').trim();
                jobs.push({
                    id: match[1],
                    title: text('.job-card-list__title, .artdeco-entity-lockup__title, strong') || (link.innerText || '').trim(),
                    company: text('.artdeco-entity-lockup__subtitle, .job-card-container__primary-description'),
                    location: text('.artdeco-entity-lockup__caption, .job-card-container__metadata-wrapper')
                });
            }
            return jobs;
        }
        """;

    private const string DetailScript = """
        () => {
            const text = (selector) => (document.querySelector(selector)?.innerText || '').trim();
            const pane = document.querySelector('.jobs-search__job-details--container, .jobs-details, .job-view-layout');
            const inPane = (selector) => (pane?.querySelector(selector)?.innerText || '').trim();
            return {
                title: inPane('.job-details-jobs-unified-top-card__job-title, .jobs-unified-top-card__job-title, h1')
                    || text('.job-details-jobs-unified-top-card__job-title, .jobs-unified-top-card__job-title, h1'),
                company: inPane('.job-details-jobs-unified-top-card__company-name, .jobs-unified-top-card__company-name')
                    || text('.job-details-jobs-unified-top-card__company-name, .jobs-unified-top-card__company-name'),
                location: inPane('.job-details-jobs-unified-top-card__tertiary-description-container, .job-details-jobs-unified-top-card__primary-description-container')
                    || text('.job-details-jobs-unified-top-card__tertiary-description-container, .jobs-unified-top-card__bullet'),
                description: text('#job-details') || text('.jobs-description__content') || text('.jobs-box__html-content')
            };
        }
        """;

    private sealed class JobCard
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Location { get; set; } = "";
    }

    private sealed class JobDetail
    {
        public string Title { get; set; } = "";
        public string Company { get; set; } = "";
        public string Location { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private static readonly JsonSerializerOptions FieldJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static async Task<IReadOnlyList<FormField>> ReadFieldsAsync(IPage page)
    {
        var fields = new List<FormField>();
        var frames = page.Frames;
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex];
            if (IsCaptchaFrame(frame.Url))
            {
                continue;
            }

            try
            {
                var scope = await FormScopeAsync(frame);
                await scope.Locator("[data-apply-field]").EvaluateAllAsync<object>(
                    "elements => elements.forEach(element => element.removeAttribute('data-apply-field'))");
                var json = await scope.Locator("input, select, textarea, [role='combobox'], [role='radio'], [role='checkbox'], [aria-haspopup='listbox']")
                    .EvaluateAllAsync<string>(DescribeElementsScript, frameIndex);
                fields.AddRange(ParseFields(json));
            }
            catch (PlaywrightException)
            {
            }
        }

        return fields;
    }

    private static bool IsCaptchaFrame(string url)
    {
        return url.Contains("recaptcha", StringComparison.OrdinalIgnoreCase)
            || url.Contains("hcaptcha", StringComparison.OrdinalIgnoreCase)
            || url.Contains("captcha", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> VisibleCaptchaAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>(
                """
                () => {
                    const visible = (el) => {
                        const box = el.getBoundingClientRect();
                        const style = getComputedStyle(el);
                        return box.width > 20 && box.height > 20 && style.visibility !== 'hidden' && style.display !== 'none' && style.opacity !== '0';
                    };
                    return [...document.querySelectorAll('iframe, [id*="captcha" i], [class*="captcha" i]')]
                        .some(el => /recaptcha|hcaptcha|captcha/i.test((el.src || '') + ' ' + (el.id || '') + ' ' + String(el.className || '')) && visible(el));
                }
                """);
        }
        catch (PlaywrightException)
        {
            return page.Frames.Any(frame => IsCaptchaFrame(frame.Url));
        }
    }

    internal static IReadOnlyList<FormField> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        var raw = JsonSerializer.Deserialize<List<RawField>>(json, FieldJson) ?? [];
        return raw
            .Select(field =>
            {
                var label = FirstText(field.Label, field.Group, field.Placeholder, field.Name);
                var kind = FirstText(field.Kind, field.Type, "text");
                var visual = string.IsNullOrWhiteSpace(field.Label)
                    && string.IsNullOrWhiteSpace(field.Placeholder)
                    && string.IsNullOrWhiteSpace(field.Name)
                    && string.IsNullOrWhiteSpace(field.Group);
                return new FormField(
                    label,
                    kind,
                    field.Options ?? [],
                    field.Key ?? "",
                    field.Tag ?? "",
                    field.Placeholder?.Trim() ?? "",
                    field.Name?.Trim() ?? "",
                    field.Required,
                    field.Group?.Trim() ?? "",
                    kind.Equals("password", StringComparison.OrdinalIgnoreCase) ? "" : field.CurrentValue?.Trim() ?? "",
                    visual);
            })
            .Where(field => !string.IsNullOrWhiteSpace(field.Key))
            .ToList();
    }

    private static string FirstText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static async Task PrepareCompanyPageAsync(IPage page)
    {
        await page.BringToFrontAsync();
        try
        {
            await page.EvaluateAsync("""
                () => {
                    document.documentElement.style.overflow = 'auto';
                    document.body.style.overflow = 'auto';
                    const text = (el) => ((el.innerText || el.value || el.getAttribute('aria-label') || '')).replace(/\s+/g, ' ').trim();
                    const accept = [...document.querySelectorAll('button, a, input[type="button"], input[type="submit"], [role="button"]')]
                        .find(el => /accept all cookies|accept all|accept cookies|i agree|allow all/i.test(text(el)));
                    if (!accept) return;
                    accept.scrollIntoView({ block: 'center' });
                    accept.click();
                }
                """);
            await page.WaitForTimeoutAsync(600);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
        }
    }

    private async Task<AgentPage> ReadAgentPageForApplyAsync(IPage page, bool includeScreenshot, CancellationToken cancellationToken)
    {
        var seen = await ReadAgentPageAsync(page, includeScreenshot, cancellationToken);
        if (seen.Wall == "captcha")
        {
            return seen;
        }

        if (seen.Buttons.Any(button => button.Contains("Back to Job Posting", StringComparison.OrdinalIgnoreCase)))
        {
            var deadline = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < deadline && seen.Fields.Count == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                seen = await ReadAgentPageAsync(page, includeScreenshot, cancellationToken);
            }

            return seen;
        }

        return seen;
    }

    private async Task<bool> WaitForHumanCaptchaAsync(IPage page, CancellationToken cancellationToken)
    {
        await page.BringToFrontAsync();
        try
        {
            await page.EvaluateAsync("() => { document.title = 'Solve the check in this window'; }");
        }
        catch (PlaywrightException)
        {
        }

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DetectWallAsync(page, []) != "captcha")
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return false;
    }

    private static async Task<AgentPage> ReadAgentPageAsync(IPage page, bool includeScreenshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fields = await ReadFieldsAsync(page);
        var buttons = await ReadButtonsAsync(page);
        var wall = await DetectWallAsync(page, fields);
        var surface = fields.Count == 0 && await CountCanvasAsync(page) > 0 ? "canvas" : "";
        var screenshot = "";
        if (includeScreenshot)
        {
            try
            {
                var bytes = await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Type = ScreenshotType.Jpeg,
                    Quality = 40
                });
                screenshot = Convert.ToBase64String(bytes);
            }
            catch (PlaywrightException)
            {
            }
        }

        return new AgentPage(fields, buttons, screenshot, page.Url, wall, surface);
    }

    private static async Task<bool> ClickAdvanceAsync(IPage page, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var frame in page.Frames)
        {
            try
            {
                if (!await frame.EvaluateAsync<bool>(ClickAdvanceScript))
                {
                    continue;
                }
            }
            catch (PlaywrightException)
            {
                continue;
            }

            try
            {
                await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions
                {
                    Timeout = 8_000
                });
            }
            catch (TimeoutException)
            {
            }

            await page.WaitForTimeoutAsync(1200);
            return true;
        }

        return false;
    }

    private static async Task<bool> ClickCompanyApplyAsync(IPage page)
    {
        var apply = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            NameRegex = new System.Text.RegularExpressions.Regex(@"Easy Apply", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        });
        if (await apply.CountAsync() == 0)
        {
            apply = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                NameRegex = new System.Text.RegularExpressions.Regex(@"^Apply(?: now)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            });
        }
        if (await apply.CountAsync() == 0)
        {
            apply = page.GetByRole(AriaRole.Link, new PageGetByRoleOptions
            {
                NameRegex = new System.Text.RegularExpressions.Regex(@"^Apply(?: now)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            });
        }

        if (await apply.CountAsync() == 0)
        {
            return false;
        }

        await apply.First.ClickAsync(new LocatorClickOptions { Timeout = 8_000, Force = true });
        return true;
    }

    internal static async Task<string> PerformFormToolAsync(IPage page, FormToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return call.Tool switch
            {
                "fill_experience" => await FillExperienceAsync(page, call.Jobs),
                "click_named" => await ClickNamedAsync(page, call.Value),
                "fill_text" => await FillTextAsync(page, call.FieldId, call.Value),
                "select_option" => await SelectOptionAsync(page, call.FieldId, call.Value),
                "choose_radio_or_checkbox" => await ChooseCheckAsync(page, call.FieldId, call.Checked),
                "pick_custom_dropdown" => await PickDropdownAsync(page, call.FieldId, call.Value),
                "upload_file" => UploadFileResult(call.Value),
                "next_or_submit" => await ClickNamedButtonAsync(page, call.Value),
                "read_page_text" => await ReadCombinedTextAsync(page),
                "click_point" => await ClickPointAsync(page, call.X, call.Y, call.Value),
                "screenshot_for_review" => await ReviewScreenshotAsync(page),
                _ => "That form action is not available."
            };
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            return "The control could not be used. " + exception.Message;
        }
    }

    private static async Task<string> FillTextAsync(IPage page, string fieldId, string value)
    {
        await ApplicationPace.PauseBetweenFieldsAsync();
        var box = FieldLocator(page, fieldId);
        if (await box.CountAsync() == 0)
        {
            return "Field " + fieldId + " was not found.";
        }

        var kind = await box.First.EvaluateAsync<string>("el => (el.getAttribute('type') || '').toLowerCase()");
        if (kind == "password")
        {
            return "Password fields are left blank.";
        }

        await box.First.ScrollIntoViewIfNeededAsync();
        await box.First.FillAsync(value);
        return "Typed into field " + fieldId + ".";
    }

    private static async Task<string> SelectOptionAsync(IPage page, string fieldId, string optionText)
    {
        var box = FieldLocator(page, fieldId);
        if (await box.CountAsync() == 0)
        {
            return "Field " + fieldId + " was not found.";
        }

        var matched = await box.First.EvaluateAsync<string>(
            """
            (el, wanted) => {
                if (!el.options) return '';
                const text = (wanted || '').trim().toLowerCase();
                const options = [...el.options];
                const match = options.find(option => option.text.trim().toLowerCase() === text)
                    || options.find(option => option.text.trim().toLowerCase().includes(text))
                    || options.find(option => text.includes(option.text.trim().toLowerCase()) && option.text.trim());
                return match ? match.text.trim() : '';
            }
            """,
            optionText);
        if (string.IsNullOrWhiteSpace(matched))
        {
            return "No option matched " + optionText + ".";
        }

        await box.First.SelectOptionAsync(new SelectOptionValue { Label = matched });
        return "Selected " + matched + ".";
    }

    private static async Task SelectContainingOptionAsync(ILocator box, string value)
    {
        var options = await box.Locator("option").AllInnerTextsAsync();
        var wanted = value.Trim();
        string? match = null;
        foreach (var option in options)
        {
            var text = option.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (text.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                || text.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                || (wanted.Contains(text, StringComparison.OrdinalIgnoreCase) && text.Length >= 2))
            {
                match = text;
                break;
            }
        }

        if (match is null)
        {
            return;
        }

        await box.SelectOptionAsync(new SelectOptionValue { Label = match });
    }

    private static async Task<string> ChooseCheckAsync(IPage page, string fieldId, bool shouldCheck)
    {
        var box = FieldLocator(page, fieldId);
        if (await box.CountAsync() == 0)
        {
            return "Field " + fieldId + " was not found.";
        }

        await box.First.ScrollIntoViewIfNeededAsync();
        var kind = await box.First.EvaluateAsync<string>("el => (el.getAttribute('type') || el.getAttribute('role') || '').toLowerCase()");
        if (kind == "checkbox")
        {
            if (shouldCheck)
            {
                await box.First.CheckAsync();
            }
            else
            {
                await box.First.UncheckAsync();
            }
        }
        else if (shouldCheck)
        {
            await box.First.ClickAsync();
        }

        return shouldCheck ? "Checked field " + fieldId + "." : "Unchecked field " + fieldId + ".";
    }

    private static async Task<string> PickDropdownAsync(IPage page, string fieldId, string optionText)
    {
        var box = FieldLocator(page, fieldId);
        if (await box.CountAsync() == 0)
        {
            return "Field " + fieldId + " was not found.";
        }

        await box.First.ScrollIntoViewIfNeededAsync();
        await box.First.ClickAsync();
        await page.Keyboard.TypeAsync(optionText);
        var option = page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
        {
            NameRegex = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(optionText), System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        });
        try
        {
            await option.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 4_000 });
        }
        catch (TimeoutException)
        {
            return "No dropdown option matched " + optionText + ".";
        }

        await option.First.ClickAsync();
        return "Picked " + optionText + ".";
    }

    private static string UploadFileResult(string which)
    {
        if (which is not ("resume" or "cover_letter"))
        {
            return "Upload skipped. which must be resume or cover_letter.";
        }

        return "Upload skipped. The original " + which.Replace('_', ' ') + " file is not stored, so there is no allowlisted file to attach.";
    }

    private static async Task<string> FillExperienceAsync(IPage page, IReadOnlyList<ExperienceJob>? jobs)
    {
        if (jobs is null || jobs.Count == 0)
        {
            return "No saved work history to add.";
        }

        var filled = 0;
        for (var index = 0; index < jobs.Count; index++)
        {
            var job = jobs[index];
            var titles = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions
            {
                NameRegex = new System.Text.RegularExpressions.Regex("^Job Title", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            });
            if (await titles.CountAsync() <= index)
            {
                if (index == 0)
                {
                    if (!await ClickSectionAddAsync(page, "Work Experience"))
                    {
                        return "The Work Experience Add button was not found.";
                    }
                }
                else
                {
                    var another = page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Add Another" });
                    if (await another.CountAsync() == 0)
                    {
                        break;
                    }

                    await another.First.ClickAsync();
                }

                try
                {
                    await titles.Nth(index).WaitForAsync(new LocatorWaitForOptions { Timeout = 8_000 });
                }
                catch (TimeoutException)
                {
                    break;
                }
            }

            var title = titles.Nth(index);
            var existing = "";
            try
            {
                existing = await title.InputValueAsync();
            }
            catch (PlaywrightException)
            {
            }

            if (!string.IsNullOrWhiteSpace(existing))
            {
                filled++;
                continue;
            }

            await title.FillAsync(job.Title ?? "");
            await FillLabeledAsync(page, "^Company", job.Company, index);
            await FillLabeledAsync(page, "^Location", job.Location, index);
            await FillDateAsync(page, "formField-startDate", index, job.StartMonth, job.StartYear);
            if (job.Current)
            {
                var current = page.GetByRole(AriaRole.Checkbox, new PageGetByRoleOptions
                {
                    NameRegex = new System.Text.RegularExpressions.Regex("currently work here", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                });
                if (await current.CountAsync() > index && !await current.Nth(index).IsCheckedAsync())
                {
                    await current.Nth(index).CheckAsync();
                }
            }
            else
            {
                await FillDateAsync(page, "formField-endDate", index, job.EndMonth, job.EndYear);
            }

            var description = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions
            {
                NameRegex = new System.Text.RegularExpressions.Regex("^Role Description", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            });
            if (await description.CountAsync() > index && !string.IsNullOrWhiteSpace(job.Description))
            {
                await description.Nth(index).FillAsync(job.Description);
            }

            filled++;
            await ApplicationPace.PauseBetweenFieldsAsync();
        }

        return "Filled " + filled + " saved roles on My Experience.";
    }

    private static async Task FillLabeledAsync(IPage page, string namePattern, string? value, int index)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var box = page.GetByRole(AriaRole.Textbox, new PageGetByRoleOptions
        {
            NameRegex = new System.Text.RegularExpressions.Regex(namePattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        });
        if (await box.CountAsync() > index)
        {
            await box.Nth(index).FillAsync(value);
        }
    }

    private static async Task FillDateAsync(IPage page, string automationId, int index, string? month, string? year)
    {
        var scope = page.Locator("[data-automation-id='" + automationId + "']");
        if (await scope.CountAsync() <= index)
        {
            return;
        }

        var panel = scope.Nth(index);
        await TypeSpinAsync(panel, "Month", month);
        await TypeSpinAsync(panel, "Year", year);
    }

    private static async Task TypeSpinAsync(ILocator panel, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var box = panel.GetByRole(AriaRole.Spinbutton, new LocatorGetByRoleOptions { Name = name });
        if (await box.CountAsync() == 0)
        {
            return;
        }

        await box.First.ClickAsync();
        await box.First.FillAsync(value);
    }

    private static async Task<bool> ClickSectionAddAsync(IPage page, string section)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                if (await frame.EvaluateAsync<bool>(ClickSectionAddScript, section))
                {
                    return true;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        return false;
    }

    private const string ClickSectionAddScript = """
        (section) => {
            const wanted = (section || '').replace(/\s+/g, ' ').trim().toLowerCase();
            const heads = [...document.querySelectorAll('h2, h3, h4')];
            const head = heads.find(node => (node.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase() === wanted);
            if (!head) return false;
            let node = head.parentElement;
            for (let depth = 0; depth < 6 && node; depth++) {
                const button = [...node.querySelectorAll("button[data-automation-id='add-button']")]
                    .find(item => (item.innerText || '').replace(/\s+/g, ' ').trim().toLowerCase() === 'add');
                if (button) {
                    button.scrollIntoView({ block: 'center' });
                    button.click();
                    return true;
                }
                node = node.parentElement;
            }
            return false;
        }
        """;

    private static async Task<string> ClickNamedButtonAsync(IPage page, string buttonText)
    {
        if (IsBlockedButton(buttonText))
        {
            return "Refused to click " + buttonText + ". Submit stays for you.";
        }

        var clicked = false;
        foreach (var frame in page.Frames)
        {
            try
            {
                if (await frame.EvaluateAsync<bool>(ClickNamedButtonScript, buttonText))
                {
                    clicked = true;
                    break;
                }
            }
            catch (PlaywrightException)
            {
            }
        }

        if (!clicked)
        {
            return "The button " + buttonText + " was not found.";
        }

        if (buttonText.Contains("apply manually", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await page.WaitForSelectorAsync(
                    "[data-automation-id^='formField-']",
                    new PageWaitForSelectorOptions { Timeout = 20_000 });
            }
            catch (TimeoutException)
            {
            }
        }

        return "Clicked " + buttonText + ".";
    }

    private static async Task<string> ClickNamedAsync(IPage page, string buttonText)
    {
        return await ClickNamedButtonAsync(page, buttonText);
    }

    private static async Task<string> ReviewScreenshotAsync(IPage page)
    {
        var image = "";
        try
        {
            var bytes = await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 30 });
            image = Convert.ToBase64String(bytes);
        }
        catch (PlaywrightException)
        {
        }

        var text = await ReadCombinedTextAsync(page);
        return JsonSerializer.Serialize(new { message = "Visible page:\n" + text, image });
    }

    private static async Task<string> ClickPointAsync(IPage page, int x, int y, string value)
    {
        if (x <= 0 || y <= 0)
        {
            return "No point to click.";
        }

        var blocked = await page.EvaluateAsync<bool>(
            """
            ([x, y]) => {
                const el = document.elementFromPoint(x, y);
                const text = ((el && el.innerText) || '') + ' ' + ((el && el.getAttribute('aria-label')) || '');
                return /submit|captcha|robot|apple|google|create account/i.test(text);
            }
            """,
            new[] { x, y });
        if (blocked)
        {
            return "Refused to click that point.";
        }

        await page.Mouse.ClickAsync(x, y);
        if (!string.IsNullOrWhiteSpace(value))
        {
            await page.Keyboard.TypeAsync(value);
            return "Clicked the point and typed.";
        }

        return "Clicked the point.";
    }

    private static async Task<ILocator> FormScopeAsync(IFrame frame)
    {
        var modal = frame.Locator(".jobs-easy-apply-modal, .jobs-easy-apply-content");
        if (await modal.CountAsync() > 0)
        {
            return modal.First;
        }

        return frame.Locator("body");
    }

    private static async Task<IReadOnlyList<string>> ReadButtonsAsync(IPage page)
    {
        var buttons = new List<string>();
        foreach (var frame in page.Frames)
        {
            if (IsCaptchaFrame(frame.Url))
            {
                continue;
            }

            try
            {
                var scope = await FormScopeAsync(frame);
                var found = await scope.Locator("button, a, [role='button']").EvaluateAllAsync<string[]>(ButtonTextScript);
                buttons.AddRange(found ?? []);
            }
            catch (PlaywrightException)
            {
            }
        }

        return buttons.Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
    }

    private static async Task<int> CountCanvasAsync(IPage page)
    {
        var count = 0;
        foreach (var frame in page.Frames)
        {
            try
            {
                count += await frame.Locator("canvas").CountAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        return count;
    }

    private static async Task<string> DetectWallAsync(IPage page, IReadOnlyList<FormField> fields)
    {
        if (await VisibleCaptchaAsync(page))
        {
            return "captcha";
        }

        var text = "";
        try
        {
            text = await ReadCombinedTextAsync(page);
        }
        catch (PlaywrightException)
        {
        }

        if (text.Contains("verify you are human", StringComparison.OrdinalIgnoreCase)
            || text.Contains("i'm not a robot", StringComparison.OrdinalIgnoreCase))
        {
            return "captcha";
        }

        var password = fields.Any(field => field.Kind.Equals("password", StringComparison.OrdinalIgnoreCase));
        if (password && (text.Contains("create account", StringComparison.OrdinalIgnoreCase) || page.Url.Contains("myworkdayjobs", StringComparison.OrdinalIgnoreCase)))
        {
            return "account";
        }

        if (password && (page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)))
        {
            return "login";
        }

        return "";
    }

    private static async Task<string> ReadCombinedTextAsync(IPage page)
    {
        var parts = new List<string>();
        foreach (var frame in page.Frames)
        {
            if (IsCaptchaFrame(frame.Url))
            {
                continue;
            }

            try
            {
                parts.Add(await frame.EvaluateAsync<string>(ReadPageTextScript));
            }
            catch (PlaywrightException)
            {
            }
        }

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool IsBlockedButton(string buttonText)
    {
        var text = buttonText.Trim();
        return text.Contains("submit", StringComparison.OrdinalIgnoreCase)
            || text.Contains("apple", StringComparison.OrdinalIgnoreCase)
            || text.Contains("google", StringComparison.OrdinalIgnoreCase)
            || text.Contains("create account", StringComparison.OrdinalIgnoreCase);
    }

    private static ILocator FieldLocator(IPage page, string fieldId)
    {
        var safe = new string((fieldId ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (safe.Length == 0)
        {
            safe = "missing";
        }

        var frame = page.MainFrame;
        var match = System.Text.RegularExpressions.Regex.Match(fieldId ?? "", @"^f(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var index) && index >= 0 && index < page.Frames.Count)
        {
            frame = page.Frames[index];
        }

        return frame.Locator("[data-apply-field='" + safe + "']");
    }

    private static async Task FillAnswersAsync(IPage page, IReadOnlyList<FormAnswer> answers)
    {
        foreach (var answer in answers)
        {
            if (string.IsNullOrWhiteSpace(answer.Value) || string.IsNullOrWhiteSpace(answer.Key))
            {
                continue;
            }

            await ApplicationPace.PauseBetweenFieldsAsync();
            try
            {
                var box = FieldLocator(page, answer.Key);
                if (await box.CountAsync() == 0)
                {
                    await page.EvaluateAsync(FillFieldScript, new FieldFill { Label = answer.Label, Value = answer.Value });
                    continue;
                }

                await box.First.ScrollIntoViewIfNeededAsync();
                var kind = await box.First.EvaluateAsync<string>(
                    """
                    el => {
                        const popup = (el.getAttribute('aria-haspopup') || '').toLowerCase();
                        if (popup === 'listbox') return 'combobox';
                        return (el.getAttribute('type') || el.getAttribute('role') || el.tagName || '').toLowerCase();
                    }
                    """);
                if (kind == "select")
                {
                    await SelectContainingOptionAsync(box.First, answer.Value);
                }
                else if (kind == "checkbox")
                {
                    if (!await box.First.IsCheckedAsync())
                    {
                        await box.First.CheckAsync();
                    }
                }
                else if (kind == "radio")
                {
                    await page.EvaluateAsync(FillFieldScript, new FieldFill { Label = answer.Label, Value = answer.Value });
                }
                else if (kind is "combobox" or "listbox" or "button")
                {
                    await PickDropdownAsync(page, answer.Key, answer.Value);
                }
                else
                {
                    await box.First.FillAsync(answer.Value);
                }
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
            }
        }
    }

    private static bool IsSignInPage(string url)
    {
        return url.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/challenge", StringComparison.OrdinalIgnoreCase)
            || url.Contains("authwall", StringComparison.OrdinalIgnoreCase);
    }

    private const string ReadPageTextScript = """
        () => {
            const textOf = (el) => (el.innerText || '').replace(/\s+/g, ' ').trim();
            const headings = [...document.querySelectorAll('h1, h2, h3')].map(textOf).filter(Boolean).slice(0, 8);
            const alerts = [...document.querySelectorAll('[role="alert"], [aria-invalid="true"]')].map(textOf).filter(Boolean).slice(0, 8);
            return ['Headings: ' + headings.join(' | '), 'Messages: ' + alerts.join(' | ')].join('\n').slice(0, 2000);
        }
        """;

    private const string ClickNamedButtonScript = """
        (wanted) => {
            const textOf = (el) => (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const needle = (wanted || '').replace(/\s+/g, ' ').trim().toLowerCase();
            if (!needle) return false;
            const controls = [];
            const walk = (root) => {
                if (!root || !root.querySelectorAll) return;
                root.querySelectorAll('button, a, [role="button"]').forEach(el => controls.push(el));
                root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) walk(el.shadowRoot); });
            };
            walk(document);
            const target = controls.find(el => visible(el) && textOf(el).toLowerCase() === needle)
                || controls.find(el => visible(el) && textOf(el).toLowerCase().includes(needle));
            if (!target) return false;
            target.scrollIntoView({ block: 'center' });
            target.click();
            return true;
        }
        """;

    private const string GateWaitingScript = """
        () => {
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const textOf = (el) => ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).replace(/\s+/g, ' ').trim();
            const root = document.querySelector('.jobs-easy-apply-modal, .jobs-easy-apply-content') || document;
            const controls = [];
            const walk = (node) => {
                if (!node || !node.querySelectorAll) return;
                node.querySelectorAll('button, a, [role="button"], input[type="checkbox"], [role="checkbox"]').forEach(el => controls.push(el));
                node.querySelectorAll('*').forEach(el => { if (el.shadowRoot) walk(el.shadowRoot); });
            };
            walk(root);
            const formOpen = [...root.querySelectorAll('input, textarea, select')].some(el => {
                if (!visible(el)) return false;
                const type = (el.getAttribute('type') || '').toLowerCase();
                return !['hidden', 'checkbox', 'radio', 'submit', 'button', 'file'].includes(type);
            });
            const agreement = (el) => {
                const label = textOf(el.closest('label') || el.parentElement || el);
                return /i agree/i.test(label) && /term|privacy/i.test(label) && !/communication|future opening|marketing|newsletter/i.test(label);
            };
            return controls.some(el => {
                if (!visible(el) || el.disabled) return false;
                if (el.matches('input[type="checkbox"], [role="checkbox"]')) {
                    if (!agreement(el)) return false;
                    const on = el.matches('[role="checkbox"]') ? el.getAttribute('aria-checked') === 'true' : el.checked;
                    return !on;
                }
                if (formOpen) return false;
                const label = textOf(el);
                if (/cancel|discard|\bback\b|filter/i.test(label)) return false;
                return /easy apply|\bsubmit\b|^apply(?: now)?\b/i.test(label);
            });
        }
        """;

    private const string ClickFormGateScript = """
        () => {
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const textOf = (el) => ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).replace(/\s+/g, ' ').trim();
            const root = document.querySelector('.jobs-easy-apply-modal, .jobs-easy-apply-content') || document;
            const controls = [];
            const walk = (node) => {
                if (!node || !node.querySelectorAll) return;
                node.querySelectorAll('button, a, [role="button"], input[type="checkbox"], [role="checkbox"]').forEach(el => controls.push(el));
                node.querySelectorAll('*').forEach(el => { if (el.shadowRoot) walk(el.shadowRoot); });
            };
            walk(root);
            const formOpen = [...root.querySelectorAll('input, textarea, select')].some(el => {
                if (!visible(el)) return false;
                const type = (el.getAttribute('type') || '').toLowerCase();
                return !['hidden', 'checkbox', 'radio', 'submit', 'button', 'file'].includes(type);
            });
            const agreement = (el) => {
                const label = textOf(el.closest('label') || el.parentElement || el);
                return /i agree/i.test(label) && /term|privacy/i.test(label) && !/communication|future opening|marketing|newsletter/i.test(label);
            };
            let checked = false;
            for (const el of controls) {
                if (!visible(el) || el.disabled || !agreement(el)) continue;
                const on = el.matches('[role="checkbox"]') ? el.getAttribute('aria-checked') === 'true' : el.checked;
                if (on) continue;
                el.click();
                checked = true;
            }
            const opener = formOpen ? null : controls.find(el => {
                if (!visible(el) || el.disabled || el.matches('input, [role="checkbox"]')) return false;
                const label = textOf(el);
                if (/cancel|discard|\bback\b|filter/i.test(label)) return false;
                return /easy apply|\bsubmit\b|^apply(?: now)?\b/i.test(label);
            });
            if (opener) {
                opener.scrollIntoView({ block: 'center' });
                opener.click();
                return 'button';
            }
            return checked ? 'check' : '';
        }
        """;

    private const string ClickApplyScript = """
        () => {
            const labelOf = (el) => ((el.innerText || '') + ' ' + (el.getAttribute('aria-label') || '')).replace(/\s+/g, ' ').trim();
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                return rect.width > 0 && rect.height > 0;
            };
            const primary = [...document.querySelectorAll('.jobs-apply-button')].filter(visible);
            const pool = primary.length > 0 ? primary : [...document.querySelectorAll('button, a')].filter(visible);
            const easy = pool.find(el => /easy apply/i.test(labelOf(el)));
            const apply = pool.find(el => /^apply\b/i.test(labelOf(el)) || /^apply\b/i.test(el.getAttribute('aria-label') || ''));
            const target = easy || apply;
            if (!target) return 'missing';
            target.click();
            return easy ? 'easy' : 'apply';
        }
        """;

    private const string ReadButtonsScript = """
        () => {
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const textOf = (el) => (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
            const controls = [];
            const walk = (root) => {
                if (!root || !root.querySelectorAll) return;
                root.querySelectorAll('button, a, [role="button"]').forEach(el => controls.push(el));
                root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) walk(el.shadowRoot); });
            };
            walk(document);
            return [...new Set(controls.filter(visible).map(textOf).filter(Boolean))];
        }
        """;

    private const string ClickAdvanceScript = """
        () => {
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const textOf = (el) => (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
            const controls = [];
            const walk = (root) => {
                if (!root || !root.querySelectorAll) return;
                root.querySelectorAll('button, a, [role="button"]').forEach(el => controls.push(el));
                root.querySelectorAll('*').forEach(el => { if (el.shadowRoot) walk(el.shadowRoot); });
            };
            const modal = document.querySelector('.jobs-easy-apply-modal, .jobs-easy-apply-content');
            walk(modal || document);
            const email = controls.find(el => visible(el) && /sign in with email|continue with email|use email/i.test(textOf(el)));
            const advance = controls.find(el => visible(el) && /^(next|continue|review)\b|save and continue/i.test(textOf(el)) && !/submit/i.test(textOf(el)));
            const target = email || advance;
            if (!target) return false;
            target.scrollIntoView({ block: 'center' });
            target.click();
            return true;
        }
        """;

    private const string DescribeElementsScript = """
        (elements, frameIndex) => {
            const clean = (text) => (text || '').split('\n')[0].replace(/\s+/g, ' ').replace(/\*+/g, '').replace(/:\s*$/, '').trim();
            const clip = (text) => clean(text).slice(0, 200);
            const visible = (el) => {
                const style = getComputedStyle(el);
                if (style.visibility === 'hidden' || style.display === 'none') return false;
                const rect = el.getBoundingClientRect();
                return (rect.width > 0 && rect.height > 0) || !!(el.offsetParent || el.getClientRects().length);
            };
            const tidy = (text) => clean(text).replace(/\bselect one\b/ig, '').replace(/\brequired\b/ig, '').replace(/\s+/g, ' ').trim();
            const labelFor = (el) => {
                const root = el.getRootNode();
                const byId = (id) => (root && root.getElementById && root.getElementById(id)) || document.getElementById(id);
                const aria = tidy(el.getAttribute('aria-label'));
                if (aria) return clip(aria);
                const labelledBy = el.getAttribute('aria-labelledby');
                if (labelledBy) {
                    const text = labelledBy.split(' ').map(id => byId(id)?.innerText || '').join(' ');
                    if (clean(text)) return clip(text);
                }
                if (el.labels?.length) {
                    const text = [...el.labels].map(label => label.innerText).join(' ');
                    if (clean(text)) return clip(text);
                }
                const wrap = el.closest('label');
                if (wrap && clean(wrap.innerText)) return clip(wrap.innerText);
                const cell = el.closest('td, th');
                const previous = cell?.previousElementSibling;
                if (previous && clean(previous.innerText)) return clip(previous.innerText);
                const box = el.closest('div,fieldset,li,td');
                const nearby = box ? clip(box.innerText) : '';
                return nearby.length > 80 ? '' : nearby;
            };
            const fields = [];
            let index = 0;
            for (const el of elements) {
                if (!visible(el)) continue;
                const type = (el.getAttribute('type') || el.getAttribute('role') || el.tagName).toLowerCase();
                const popup = (el.getAttribute('aria-haspopup') || '').toLowerCase();
                if (el.tagName === 'INPUT' && el.parentElement?.querySelector('button[aria-haspopup="listbox"]')) continue;
                if (['hidden', 'submit', 'button', 'image', 'search'].includes(type) && popup !== 'listbox') continue;
                const scope = el.getRootNode();
                const groupName = el.getAttribute('name') || '';
                const options = el.tagName === 'SELECT'
                    ? [...el.options].map(option => clean(option.text)).filter(Boolean)
                    : (type === 'radio' || type === 'checkbox') && groupName && scope.querySelectorAll
                        ? [...scope.querySelectorAll(`${el.tagName.toLowerCase()}[name="${CSS.escape(groupName)}"]`)].map(input => {
                            const id = input.getAttribute('id');
                            const label = id && scope.querySelector ? scope.querySelector(`label[for="${CSS.escape(id)}"]`) : input.closest('label');
                            return clean((label && label.innerText) || input.getAttribute('value') || '');
                        }).filter(Boolean)
                        : [];
                const key = 'f' + frameIndex + 'e' + index++;
                el.setAttribute('data-apply-field', key);
                const legend = clean(el.closest('fieldset')?.querySelector('legend')?.innerText || '');
                fields.push({
                    key,
                    tag: el.tagName.toLowerCase(),
                    kind: popup === 'listbox' ? 'combobox' : type,
                    label: labelFor(el) || legend,
                    placeholder: el.getAttribute('placeholder') || '',
                    name: el.getAttribute('name') || '',
                    required: !!(el.required || el.getAttribute('aria-required') === 'true'),
                    group: legend,
                    options,
                    currentValue: type === 'password' ? '' : (el.value || (popup === 'listbox' ? clean(el.innerText) : ''))
                });
            }
            return JSON.stringify(fields);
        }
        """;

    private const string ButtonTextScript = """
        (elements) => {
            const visible = (el) => {
                const rect = el.getBoundingClientRect();
                const style = getComputedStyle(el);
                return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
            };
            const textOf = (el) => (el.innerText || el.getAttribute('aria-label') || '').replace(/\s+/g, ' ').trim();
            return [...new Set(elements.filter(visible).map(textOf).filter(Boolean))].slice(0, 30);
        }
        """;

    private const string FillFieldScript = """
        (field) => {
            const modal = document.querySelector('.jobs-easy-apply-modal, .jobs-easy-apply-content, [role="dialog"]')
                || document.querySelector('form')
                || document.body;
            if (!modal) return false;
            const wanted = (field.label || '').trim().toLowerCase();
            const value = field.value || '';
            const clean = (text) => (text || '').split('\n')[0].replace(/\s+/g, ' ').trim().toLowerCase();
            const labels = [...modal.querySelectorAll('label, legend')];
            const match = labels.find(node => {
                const text = clean(node.innerText);
                return text && (text.includes(wanted) || wanted.includes(text));
            });
            const setValue = (el) => {
                el.focus();
                el.value = value;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            if (match?.tagName === 'LEGEND') {
                const group = match.closest('fieldset') || modal;
                const choice = [...group.querySelectorAll('label')].find(node => clean(node.innerText) === value.trim().toLowerCase());
                const input = choice?.querySelector('input') || (choice?.htmlFor ? group.querySelector(`#${CSS.escape(choice.htmlFor)}`) : null);
                if (input) { input.click(); return true; }
            }
            const id = match?.getAttribute('for');
            const control = (id && modal.querySelector(`#${CSS.escape(id)}`)) || match?.querySelector('input, textarea, select');
            if (!control) return false;
            if (control.type === 'checkbox' || control.getAttribute('role') === 'checkbox') {
                if (!control.checked && control.getAttribute('aria-checked') !== 'true') control.click();
                return true;
            }
            if (control.tagName === 'SELECT') {
                const option = [...control.options].find(item => clean(item.text) === value.trim().toLowerCase());
                if (!option) return false;
                control.value = option.value;
                control.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
            }
            setValue(control);
            return true;
        }
        """;

    private sealed class RawField
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Type { get; set; } = "";
        public string Tag { get; set; } = "";
        public string Placeholder { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Required { get; set; }
        public string Group { get; set; } = "";
        public string CurrentValue { get; set; } = "";
        public List<string>? Options { get; set; }
    }

    private sealed class FieldFill
    {
        public string Label { get; set; } = "";
        public string Value { get; set; } = "";
    }

    private static void EnsureSignedIn(IPage page)
    {
        var url = page.Url;
        if (url.Contains("/login", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/checkpoint", StringComparison.OrdinalIgnoreCase)
            || url.Contains("/challenge", StringComparison.OrdinalIgnoreCase)
            || url.Contains("authwall", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Sign in on the LinkedIn window, then fetch again. The password stays on LinkedIn's page.");
        }
    }

    private static string BrowserMessage(Exception exception)
    {
        if (exception.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("browserType.launch", StringComparison.OrdinalIgnoreCase))
        {
            return "The LinkedIn browser is not installed. From the project folder run: powershell src/AutomateJob.Web/bin/Debug/net9.0/playwright.ps1 install chromium";
        }

        if (exception.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return "LinkedIn stayed on the Share your profile window. Click Continue in that window if it is still open, then click Apply again.";
        }

        if (exception.Message.Contains("not an <input", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("not an <textarea", StringComparison.OrdinalIgnoreCase))
        {
            return "A dropdown on the company form could not be typed. Click Apply again.";
        }

        return "The Chrome window could not be used. Leave that window open and click Apply again.";
    }

    private async Task CloseCoreAsync()
    {
        _page = null;
        _context = null;
        if (_attachedChrome && _browser is not null)
        {
            await _browser.CloseAsync();
            _browser = null;
        }

        _attachedChrome = false;
        _playwright?.Dispose();
        _playwright = null;
    }
}

public sealed record LinkedInJob(
    string Url,
    string Title,
    string Company,
    string Location,
    string Description,
    string Posted,
    string PostedOn,
    string ApplyMethod = "");
