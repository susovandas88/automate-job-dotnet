namespace AutomateJob.Web.Jobs;

public sealed record UnmappedQuestion(string Label, string Reason);

public sealed record ApplyOutcome(
    string Status,
    IReadOnlyList<string> Filled,
    IReadOnlyList<string> Blank,
    string Note,
    IReadOnlyList<UnmappedQuestion>? Unmapped = null);
