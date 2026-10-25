namespace AutomateJob.Web.Jobs;

public static class WebEnvFile
{
    public static void Load(string contentRoot)
    {
        var path = Path.Combine(contentRoot, ".env");
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var pair in EnvFileReader.Read(path))
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(pair.Key)) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}

public static class AgentEnvFile
{
    public static string? Value(string name)
    {
        var path = Find();
        if (path is not null && File.Exists(path)
            && EnvFileReader.Read(path).TryGetValue(name, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        var fromProcess = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(fromProcess) ? null : fromProcess.Trim();
    }

    public static string? LangSmithKey(string? sessionValue = null)
    {
        var session = sessionValue?.Trim();
        if (!string.IsNullOrWhiteSpace(session) && session.StartsWith("lsv2_", StringComparison.Ordinal))
        {
            return session;
        }

        var key = Value("LANGSMITH_API_KEY");
        return key is not null && key.StartsWith("lsv2_", StringComparison.Ordinal) ? key : null;
    }

    private static string? Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var env = Path.Combine(dir.FullName, "agent", ".env");
                var marker = Path.Combine(dir.FullName, "agent", "apply_graph.py");
                if (File.Exists(marker))
                {
                    return env;
                }

                dir = dir.Parent;
            }
        }

        return null;
    }
}

internal static class EnvFileReader
{
    public static Dictionary<string, string> Read(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return values;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim().Trim('"').Trim('\'');
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }
}
