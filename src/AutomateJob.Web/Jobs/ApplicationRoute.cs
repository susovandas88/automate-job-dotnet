namespace AutomateJob.Web.Jobs;

public static class ApplicationRoute
{
    public static string Hint(string? url)
    {
        var text = url ?? "";
        if (text.Contains("greenhouse.io", StringComparison.OrdinalIgnoreCase))
        {
            return "Greenhouse has a public job board. The form is filled in your Chrome window, and you click Submit.";
        }

        if (text.Contains("lever.co", StringComparison.OrdinalIgnoreCase))
        {
            return "Lever has a public job board. The form is filled in your Chrome window, and you click Submit.";
        }

        if (text.Contains("ashbyhq.com", StringComparison.OrdinalIgnoreCase))
        {
            return "Ashby has a public job board. The form is filled in your Chrome window, and you click Submit.";
        }

        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return "This job accepts a direct email. Send it yourself. The filler does not send mail.";
        }

        if (text.Contains("linkedin.com", StringComparison.OrdinalIgnoreCase) || text.Contains("myworkdayjobs", StringComparison.OrdinalIgnoreCase))
        {
            return "This site is filled in your visible Chrome window. You click Submit.";
        }

        return "The form is filled in your visible Chrome window. You click Submit.";
    }
}
