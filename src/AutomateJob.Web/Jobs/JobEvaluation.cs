namespace AutomateJob.Web.Jobs;

public sealed record JobEvaluation(
    string JobUrl,
    string Title,
    string Company,
    string Location,
    string Description,
    int Score,
    string? Summary,
    IReadOnlyList<string> Covered,
    IReadOnlyList<string> Missing);
