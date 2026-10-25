using AutomateJob.Web;

namespace AutomateJob.Tests;

public class AppHostTests
{
    [Fact]
    public void Data_stays_under_app_data_until_data_dir_is_set()
    {
        var previous = Environment.GetEnvironmentVariable("DATA_DIR");
        Environment.SetEnvironmentVariable("DATA_DIR", null);
        try
        {
            Assert.Equal(Path.Combine("site", "App_Data", "jobs.sqlite"), AppHost.DatabasePath("site"));
            Assert.EndsWith(Path.Combine("AutomateJob", "chrome-profile"), AppHost.ChromeProfile("site"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_DIR", previous);
        }
    }

    [Fact]
    public void Data_dir_holds_the_database_and_chrome_profile()
    {
        var previousData = Environment.GetEnvironmentVariable("DATA_DIR");
        var previousHeadless = Environment.GetEnvironmentVariable("HEADLESS");
        Environment.SetEnvironmentVariable("DATA_DIR", "/data");
        Environment.SetEnvironmentVariable("HEADLESS", "true");
        try
        {
            Assert.Equal(Path.Combine("/data", "jobs.sqlite"), AppHost.DatabasePath("site"));
            Assert.Equal(Path.Combine("/data", "chrome-profile"), AppHost.ChromeProfile("site"));
            Assert.Contains("--headless=new", AppHost.ChromeArguments("/data/chrome-profile"));
            Assert.Contains("--no-sandbox", AppHost.ChromeArguments("/data/chrome-profile"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("HEADLESS", previousHeadless);
        }
    }
}
