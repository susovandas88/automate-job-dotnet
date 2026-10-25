using System.Net;
using System.Text.RegularExpressions;

namespace AutomateJob.Web.Jobs;

public sealed class LinkedInJobBoard
{
    private const int PageSize = 10;
    private const int MaxResults = 1000;
    private static readonly Regex JobIdPattern = new(@"urn:li:jobPosting:(\d+)", RegexOptions.Compiled);
    private static readonly Regex TitlePattern = new(
        @"base-search-card__title"">\s*(.*?)\s*</h3>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CompanyPattern = new(
        @"base-search-card__subtitle"".*?<a[^>]*>\s*(.*?)\s*</a>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LocationPattern = new(
        @"job-search-card__location"">\s*(.*?)\s*</span>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex PostedPattern = new(
        @"job-search-card__listdate[^""]*""[^>]*datetime=""([^""]*)""[^>]*>\s*(.*?)\s*</time>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex DescriptionPattern = new(
        @"show-more-less-html__markup[^>]*>(.*?)</div>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly HttpClient _http;

    public LinkedInJobBoard(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<LinkedInJob>> SearchAsync(
        string keywords,
        string location,
        CancellationToken cancellationToken,
        Action<LinkedInJob>? onListed = null)
    {
        var jobs = new List<LinkedInJob>();
        for (var start = 0; start < MaxResults; start += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (start > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }

            var page = await GetAsync(ListUrl(keywords, location, start), cancellationToken);
            var parsed = ParseList(page);
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
            throw new InvalidOperationException("LinkedIn returned no jobs for that search. Change the keywords or location and try again.");
        }

        return jobs;
    }

    public async Task<string> ReadDescriptionAsync(string jobUrl, CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1200), cancellationToken);
        var id = JobId(jobUrl);
        var page = await GetAsync("https://www.linkedin.com/jobs-guest/jobs/api/jobPosting/" + id, cancellationToken);
        return ParseDescription(page);
    }

    internal static IReadOnlyList<LinkedInJob> ParseList(string html)
    {
        var matches = JobIdPattern.Matches(html);
        var jobs = new List<LinkedInJob>();
        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : html.Length;
            var chunk = html[start..end];
            var id = matches[index].Groups[1].Value;
            var title = Clean(MatchOrEmpty(TitlePattern, chunk));
            var company = Clean(MatchOrEmpty(CompanyPattern, chunk));
            var location = Clean(MatchOrEmpty(LocationPattern, chunk));
            var posted = PostedPattern.Match(chunk);
            var postedOn = posted.Success ? posted.Groups[1].Value.Trim() : "";
            var postedText = FormatPosted(postedOn, posted.Success ? posted.Groups[2].Value : "");
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            jobs.Add(new LinkedInJob(
                "https://www.linkedin.com/jobs/view/" + id + "/",
                title,
                string.IsNullOrWhiteSpace(company) ? "Unknown company" : company,
                location,
                "",
                postedText,
                postedOn,
                ApplyMethod(chunk)));
        }

        return jobs;
    }

    internal static string ApplyMethod(string html)
    {
        return html.Contains("Easy Apply", StringComparison.OrdinalIgnoreCase) ? "Easy Apply" : "Apply";
    }

    internal static string ParseDescription(string html)
    {
        var match = DescriptionPattern.Match(html);
        if (!match.Success)
        {
            return "";
        }

        var text = match.Groups[1].Value;
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>|</li>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        text = Regex.Replace(text, @"[ ]{2,}", " ");
        return text.Trim();
    }

    private async Task<string> GetAsync(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"LinkedIn did not return that page ({(int)response.StatusCode}). Fetch again in a minute.");
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }

        throw new InvalidOperationException("LinkedIn is limiting requests. Fetch again in a minute.");
    }

    private static string ListUrl(string keywords, string location, int start)
    {
        return "https://www.linkedin.com/jobs-guest/jobs/api/seeMoreJobPostings/search?keywords="
            + Uri.EscapeDataString(keywords)
            + "&location="
            + Uri.EscapeDataString(location)
            + "&start="
            + start;
    }

    private static string JobId(string jobUrl)
    {
        var match = Regex.Match(jobUrl, @"/jobs/view/(?:[^/?]*-)?(\d+)");
        if (!match.Success)
        {
            throw new InvalidOperationException("That LinkedIn job link has no job id.");
        }

        return match.Groups[1].Value;
    }

    private static string MatchOrEmpty(Regex pattern, string html)
    {
        var match = pattern.Match(html);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string FormatPosted(string postedOn, string relative)
    {
        relative = Clean(relative);
        if (DateTime.TryParseExact(postedOn, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed))
        {
            var day = parsed.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(relative) ? day : relative + " · " + day;
        }

        return relative;
    }

    private static string Clean(string html)
    {
        var text = Regex.Replace(html, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}
