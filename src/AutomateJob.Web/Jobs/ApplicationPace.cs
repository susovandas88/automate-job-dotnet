using System.Text.Json;

namespace AutomateJob.Web.Jobs;

public sealed class ApplicationPace
{
    private readonly string _path;
    private readonly object _gate = new();

    public ApplicationPace(IWebHostEnvironment environment)
        : this(Path.Combine(AppHost.DataRoot(environment.ContentRootPath), "pace.json"))
    {
    }

    internal ApplicationPace(string path)
    {
        _path = path;
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }

    public string? BlockReason()
    {
        return null;
    }

    public TimeSpan GapBeforeNext()
    {
        lock (_gate)
        {
            var pace = Read();
            if (pace.Day != Today() || pace.Count == 0 || string.IsNullOrWhiteSpace(pace.LastUtc))
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(Random.Shared.Next(20, 51));
        }
    }

    public void MarkStarted()
    {
        lock (_gate)
        {
            var pace = Read();
            if (pace.Day != Today())
            {
                pace = new PaceFile(Today(), 0, "");
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(pace with
            {
                Count = pace.Count + 1,
                LastUtc = DateTimeOffset.UtcNow.ToString("O")
            }));
        }
    }

    public static Task PauseBetweenFieldsAsync()
    {
        return Task.Delay(Random.Shared.Next(600, 1601));
    }

    private PaceFile Read()
    {
        if (!File.Exists(_path))
        {
            return new PaceFile(Today(), 0, "");
        }

        try
        {
            return JsonSerializer.Deserialize<PaceFile>(File.ReadAllText(_path)) ?? new PaceFile(Today(), 0, "");
        }
        catch (JsonException)
        {
            return new PaceFile(Today(), 0, "");
        }
    }

    private static string Today()
    {
        return DateTime.Now.ToString("yyyy-MM-dd");
    }

    private sealed record PaceFile(string Day, int Count, string LastUtc);
}
