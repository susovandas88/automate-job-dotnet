namespace AutomateJob.Web.Matching;

public sealed record MatchReport(
    int Score,
    string Verdict,
    string Summary,
    IReadOnlyList<string> Matched,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> PreferredMatched,
    IReadOnlyList<string> PreferredMissing,
    string ScoredBy)
{
    public bool HasPreferred => PreferredMatched.Count > 0 || PreferredMissing.Count > 0;
}
