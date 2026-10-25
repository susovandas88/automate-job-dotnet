namespace AutomateJob.Web.Jobs;

public static class JobRelevance
{
    public const int MinimumScore = 55;

    public static bool IsVisible(StoredJob job, bool showWeakMatches)
    {
        if (showWeakMatches || !string.IsNullOrWhiteSpace(job.Error) || job.Score is null)
        {
            return true;
        }

        return job.Score >= MinimumScore;
    }
}
