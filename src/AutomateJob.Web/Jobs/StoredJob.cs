namespace AutomateJob.Web.Jobs;

public sealed record StoredJob(
    long Id,
    string JobUrl,
    string Title,
    string Company,
    string Location,
    string Posted,
    int? Score,
    string? Summary,
    IReadOnlyList<string> Covered,
    IReadOnlyList<string> Missing,
    string? Error,
    string ApplyMethod = "");
