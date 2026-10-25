namespace AutomateJob.Web.Jobs;

internal static class LinkedInText
{
    public static string Title(string? value)
    {
        return FirstUsefulLine(value);
    }

    public static string Company(string? value)
    {
        return FirstUsefulLine(value);
    }

    public static string Location(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var parts = value.Split(['·', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (IsMeta(part))
            {
                continue;
            }

            return part;
        }

        return "";
    }

    private static string FirstUsefulLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var line = value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "" : line;
    }

    private static bool IsMeta(string value)
    {
        return value.Contains("ago", StringComparison.OrdinalIgnoreCase)
            || value.Contains("applicant", StringComparison.OrdinalIgnoreCase)
            || value.Contains("reposted", StringComparison.OrdinalIgnoreCase)
            || value.Contains("promoted", StringComparison.OrdinalIgnoreCase)
            || value.Contains("easy apply", StringComparison.OrdinalIgnoreCase);
    }
}
