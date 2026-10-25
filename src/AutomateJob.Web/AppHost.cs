namespace AutomateJob.Web;

public static class AppHost
{
    public static string DataRoot(string contentRoot)
    {
        var data = Environment.GetEnvironmentVariable("DATA_DIR");
        return string.IsNullOrWhiteSpace(data)
            ? Path.Combine(contentRoot, "App_Data")
            : data.Trim();
    }

    public static string DatabasePath(string contentRoot)
    {
        return Path.Combine(DataRoot(contentRoot), "jobs.sqlite");
    }

    public static string ChromeProfile(string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATA_DIR")))
        {
            return Path.Combine(DataRoot(contentRoot), "chrome-profile");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomateJob",
            "chrome-profile");
    }

    public static bool Headless()
    {
        return string.Equals(Environment.GetEnvironmentVariable("HEADLESS"), "true", StringComparison.OrdinalIgnoreCase);
    }

    public static string ChromePath()
    {
        var configured = Environment.GetEnvironmentVariable("CHROME_PATH");
        return string.IsNullOrWhiteSpace(configured)
            ? @"C:\Program Files\Google\Chrome\Application\chrome.exe"
            : configured.Trim();
    }

    public static string ChromeArguments(string profileDirectory)
    {
        var args = "--remote-debugging-port=9222 --user-data-dir=\"" + profileDirectory + "\"";
        if (Headless())
        {
            args += " --headless=new --no-sandbox --disable-dev-shm-usage";
        }

        return args;
    }
}
