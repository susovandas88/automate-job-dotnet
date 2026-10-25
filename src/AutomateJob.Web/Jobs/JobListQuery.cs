using System.Globalization;

namespace AutomateJob.Web.Jobs;

public static class JobListQuery
{
    public const int PageSize = 25;

    public static IReadOnlyList<StoredJob> Apply(
        IReadOnlyList<StoredJob> jobs,
        bool showWeakMatches,
        DateOnly? from,
        DateOnly? to,
        string? location,
        int page,
        out int total,
        out int pageCount,
        out int pageNumber)
    {
        if (from is not null && to is not null && from > to)
        {
            (from, to) = (to, from);
        }

        var filtered = jobs
            .Where(job => JobRelevance.IsVisible(job, showWeakMatches) && InRange(job, from, to) && InLocation(job, location))
            .ToList();
        total = filtered.Count;
        pageCount = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        pageNumber = page < 1 ? 1 : page;
        if (pageNumber > pageCount)
        {
            pageNumber = pageCount;
        }

        return filtered.Skip((pageNumber - 1) * PageSize).Take(PageSize).ToList();
    }

    internal static bool InRange(StoredJob job, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null)
        {
            return true;
        }

        var posted = PostedDate(job.Posted);
        if (posted is null)
        {
            return false;
        }

        if (from is not null && posted < from)
        {
            return false;
        }

        return to is null || posted <= to;
    }

    public static IReadOnlyList<string> Locations(IEnumerable<StoredJob> jobs, string? selected = null)
    {
        var places = jobs
            .Select(job => job.Location.Trim())
            .Where(location => location.Length > 0)
            .ToList();
        if (!string.IsNullOrWhiteSpace(selected))
        {
            places.Add(selected.Trim());
        }

        return places
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(location => location, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool InLocation(StoredJob job, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return true;
        }

        return job.Location.Contains(location.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static DateOnly? PostedDate(string posted)
    {
        var separator = posted.LastIndexOf('·');
        var tail = (separator >= 0 ? posted[(separator + 1)..] : posted).Trim();
        return DateOnly.TryParseExact(tail, "d MMM yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }
}
