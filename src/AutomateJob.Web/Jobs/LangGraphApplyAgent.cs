using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutomateJob.Web.Matching;
using Microsoft.Playwright;

namespace AutomateJob.Web.Jobs;

public sealed class LangGraphApplyAgent
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _repoRoot;
    private readonly string _contentRoot;

    public LangGraphApplyAgent(IWebHostEnvironment environment)
    {
        _repoRoot = FindRepoRoot(environment.ContentRootPath);
        _contentRoot = environment.ContentRootPath;
    }

    public async Task<ApplyOutcome> RunAsync(
        Func<bool, CancellationToken, Task<AgentPage>> observe,
        Func<IReadOnlyList<FormAnswer>, CancellationToken, Task> fill,
        Func<CancellationToken, Task<bool>> advance,
        Func<CancellationToken, Task<bool>> clickApply,
        Func<FormToolCall, CancellationToken, Task<string>> act,
        Func<CancellationToken, Task<(bool Opened, bool Waiting)>> openGate,
        Func<int, CancellationToken, Task<bool>> reopenAttempt,
        Func<CancellationToken, Task<bool>> tabOpen,
        Func<CancellationToken, Task<bool>> waitForHuman,
        string resume,
        ApplicantProfile profile,
        string apiKey,
        string? langsmithApiKey,
        IReadOnlyList<SavedAnswer> answers,
        CancellationToken cancellationToken)
    {
        var python = PythonPath();
        if (!File.Exists(Path.Combine(_repoRoot, "agent", "apply_graph.py")))
        {
            throw new InvalidOperationException("The LangGraph agent is missing from the agent folder.");
        }

        var logDirectory = Path.Combine(AppHost.DataRoot(_contentRoot), "langgraph");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        File.WriteAllText(logPath, $"LangGraph run {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
        File.WriteAllText(Path.Combine(logDirectory, "latest.txt"), logPath + Environment.NewLine);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        var token = timeout.Token;

        var start = new ProcessStartInfo
        {
            FileName = python,
            WorkingDirectory = _repoRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add("-m");
        start.ArgumentList.Add("agent.apply_graph");
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONPATH"] = _repoRoot;
        foreach (var name in new[]
        {
            "OPENAI_API_KEY",
            "OPENAI_MODEL",
            "OPENAI_BASE_URL",
            "OPENAI_ENABLED",
            "GEMINI_API_KEY",
            "GEMINI_MODEL",
            "GEMINI_BASE_URL",
            "LANGSMITH_API_KEY",
            "LANGSMITH_TRACING",
            "LANGSMITH_PROJECT",
            "LANGSMITH_ENDPOINT",
            "LANGCHAIN_API_KEY",
            "LANGCHAIN_TRACING_V2",
            "LANGCHAIN_PROJECT",
            "LANGCHAIN_ENDPOINT"
        })
        {
            start.Environment.Remove(name);
        }

        var openAi = AutomateJob.Web.Matching.LlmRoute.AgentOpenAiEnabled();
        start.Environment["OPENAI_ENABLED"] = openAi ? "true" : "false";
        CopyAgentSetting(start, "OPENAI_MODEL");
        CopyAgentSetting(start, "OPENAI_BASE_URL");
        CopyAgentSetting(start, "GEMINI_API_KEY");
        CopyAgentSetting(start, "GEMINI_MODEL");
        CopyAgentSetting(start, "GEMINI_BASE_URL");
        if (openAi && !string.IsNullOrWhiteSpace(apiKey))
        {
            start.Environment["OPENAI_API_KEY"] = apiKey;
        }

        if (!string.IsNullOrWhiteSpace(langsmithApiKey) && langsmithApiKey.StartsWith("lsv2_", StringComparison.Ordinal))
        {
            start.Environment["LANGSMITH_API_KEY"] = langsmithApiKey;
            start.Environment["LANGSMITH_TRACING"] = "true";
            start.Environment["LANGSMITH_PROJECT"] = "automate-job";
            start.Environment["LANGSMITH_ENDPOINT"] = "https://api.smith.langchain.com";
        }

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("The LangGraph agent could not be started.");
        }

        process.BeginErrorReadLine();
        var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new
        {
            resume,
            logPath,
            profile = new
            {
                firstName = profile.FirstName,
                lastName = profile.LastName,
                email = profile.Email,
                phone = profile.Phone,
                city = profile.City,
                country = profile.Country,
                linkedInUrl = profile.LinkedInUrl,
                website = profile.Website,
                currentTitle = profile.CurrentTitle,
                yearsOfExperience = profile.YearsOfExperience,
                workAuthorization = profile.WorkAuthorization,
                noticePeriod = profile.NoticePeriod,
                expectedSalary = profile.ExpectedSalary,
                willingToRelocate = profile.WillingToRelocate,
                education = profile.Education,
                workHistory = profile.WorkHistory,
                skills = profile.Skills,
                additionalDetails = profile.AdditionalDetails
            },
            answers = answers.Select(answer => new { question = answer.Question, answer = answer.Answer })
        }));

        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(token);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var message = JsonSerializer.Deserialize<AgentMessage>(line, Json);
                if (message is null || string.IsNullOrWhiteSpace(message.Tool))
                {
                    continue;
                }

                if (message.Tool == "done")
                {
                    await WaitForExitAsync(process, token);
                    AppendLog(logPath, Redact(stderr.ToString()));
                    return new ApplyOutcome(
                        string.IsNullOrWhiteSpace(message.Status) ? "Filled" : message.Status,
                        message.Filled ?? [],
                        message.Blank ?? [],
                        WithLog(
                            string.IsNullOrWhiteSpace(message.Note)
                                ? "The OpenAI LangGraph agent filled the company form. Submit it in that tab if you want to send it."
                                : message.Note,
                            logPath),
                        (message.Unmapped ?? [])
                            .Where(item => !string.IsNullOrWhiteSpace(item.Label))
                            .Select(item => new UnmappedQuestion(item.Label!, item.Reason ?? ""))
                            .ToList());
                }

                var ok = true;
                if (message.Tool == "observe")
                {
                    var page = await observe(message.IncludeScreenshot ?? true, token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new
                    {
                        fields = page.Fields.Select(field => new
                        {
                            key = field.Key,
                            tag = field.Tag,
                            kind = field.Kind,
                            label = field.Label,
                            placeholder = field.Placeholder,
                            name = field.Name,
                            required = field.Required,
                            group = field.Group,
                            options = field.Options,
                            currentValue = field.CurrentValue,
                            visual = field.Visual
                        }),
                        buttons = page.Buttons,
                        screenshot = page.Screenshot,
                        url = page.Url,
                        wall = page.Wall,
                        surface = page.Surface
                    }));
                    continue;
                }

                if (message.Tool == "fill")
                {
                    var formAnswers = (message.Actions ?? [])
                        .Where(action => !string.IsNullOrWhiteSpace(action.Value))
                        .Select(action => new FormAnswer(action.Label ?? "", action.Value, action.Key ?? ""))
                        .ToList();
                    await fill(formAnswers, token);
                }
                else if (message.Tool == "click_advance")
                {
                    ok = await advance(token);
                    AppendLog(logPath, "host click advance " + (ok ? "ok" : "not found"));
                }
                else if (message.Tool == "fill_experience")
                {
                    var result = await act(new FormToolCall("fill_experience", "", "", false, Jobs: message.Jobs), token);
                    AppendLog(logPath, "host fill experience " + Redact(result));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, message = result }));
                    continue;
                }
                else if (message.Tool == "click_named")
                {
                    var result = await act(new FormToolCall("click_named", "", message.ButtonText ?? "", false), token);
                    ok = result.StartsWith("Clicked", StringComparison.Ordinal);
                    AppendLog(logPath, "host click named " + Redact(result));
                }
                else if (message.Tool == "click_apply")
                {
                    ok = await clickApply(token);
                    AppendLog(logPath, "host click apply " + (ok ? "ok" : "not found"));
                }
                else if (message.Tool == "open_gate")
                {
                    var gate = await openGate(token);
                    AppendLog(logPath, "host open gate opened=" + gate.Opened + " waiting=" + gate.Waiting);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { opened = gate.Opened, waiting = gate.Waiting }));
                    continue;
                }
                else if (message.Tool == "reopen_attempt")
                {
                    ok = await reopenAttempt(message.Attempt ?? 1, token);
                    AppendLog(logPath, "host reopen attempt " + (message.Attempt ?? 1) + " " + (ok ? "ok" : "not found"));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok }));
                    continue;
                }
                else if (message.Tool == "tab_open")
                {
                    var open = await tabOpen(token);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { open }));
                    continue;
                }
                else if (message.Tool == "wait_for_human")
                {
                    var solved = await waitForHuman(token);
                    AppendLog(logPath, "host wait for human solved=" + solved);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { solved }));
                    continue;
                }
                else if (message.Tool == "screenshot_for_review")
                {
                    var review = await act(new FormToolCall(message.Tool, "", "", false), token);
                    var image = "";
                    var text = review;
                    if (review.StartsWith('{'))
                    {
                        var reviewJson = JsonSerializer.Deserialize<ReviewMessage>(review, Json);
                        text = reviewJson?.Message ?? review;
                        image = reviewJson?.Image ?? "";
                    }

                    AppendLog(logPath, "host screenshot_for_review " + Redact(text));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, message = text, image }));
                    continue;
                }
                else if (message.Tool is "fill_text" or "select_option" or "choose_radio_or_checkbox" or "pick_custom_dropdown"
                    or "upload_file" or "next_or_submit" or "read_page_text" or "click_point")
                {
                    var result = await act(new FormToolCall(
                        message.Tool,
                        message.FieldId ?? "",
                        message.Value ?? message.OptionText ?? message.Which ?? message.ButtonText ?? "",
                        message.Checked == true,
                        message.X ?? 0,
                        message.Y ?? 0), token);
                    AppendLog(logPath, "host " + message.Tool + " " + Redact(result));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok = true, message = result }));
                    continue;
                }

                await writer.WriteLineAsync(JsonSerializer.Serialize(new { ok }));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Stop(process);
            AppendLog(logPath, "host timed out");
            throw new TimeoutException("The LangGraph agent took too long on the company form. Log: " + logPath);
        }
        finally
        {
            if (!process.HasExited)
            {
                Stop(process);
            }
        }

        var error = Redact(stderr.ToString()).Trim();
        AppendLog(logPath, error);
        throw new InvalidOperationException(
            (string.IsNullOrWhiteSpace(error)
                ? "The LangGraph agent stopped before the company form was finished."
                : error)
            + " Log: " + logPath);
    }

    private static void CopyAgentSetting(ProcessStartInfo start, string name)
    {
        var value = AgentEnvFile.Value(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            start.Environment[name] = value;
        }
    }

    private string PythonPath()
    {
        var configured = Environment.GetEnvironmentVariable("PYTHON");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim();
            if (!File.Exists(path))
            {
                throw new InvalidOperationException("PYTHON is set but the interpreter was not found at " + path + ".");
            }

            return path;
        }

        var windows = Path.Combine(_repoRoot, "agent", ".venv", "Scripts", "python.exe");
        if (File.Exists(windows))
        {
            return windows;
        }

        var linux = Path.Combine(_repoRoot, "agent", ".venv", "bin", "python");
        if (File.Exists(linux))
        {
            return linux;
        }

        throw new InvalidOperationException(
            "The OpenAI LangGraph agent is not installed. In the project folder run: py -3 -m venv agent/.venv ; agent/.venv/Scripts/python.exe -m pip install -r agent/requirements.txt");
    }

    private static async Task WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        using var exit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exit.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(exit.Token);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static string Redact(string text)
    {
        return Regex.Replace(
            Regex.Replace(text, @"sk-[A-Za-z0-9_\-]+", "sk-..."),
            @"lsv2_[A-Za-z0-9_\-]+",
            "lsv2_...");
    }

    private static void AppendLog(string path, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {message.Trim()}{Environment.NewLine}");
    }

    private static string WithLog(string note, string logPath)
    {
        return note + " Log: " + logPath;
    }

    private static string FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutomateJob.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return start;
    }

    private sealed class AgentMessage
    {
        public string? Tool { get; set; }
        public string? Status { get; set; }
        public string? Note { get; set; }
        public string? FieldId { get; set; }
        public string? Value { get; set; }
        public string? OptionText { get; set; }
        public string? Which { get; set; }
        public string? ButtonText { get; set; }
        public bool? Checked { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public List<string>? Filled { get; set; }
        public List<string>? Blank { get; set; }
        public List<UnmappedMessage>? Unmapped { get; set; }
        public List<AgentAction>? Actions { get; set; }
        public List<ExperienceJob>? Jobs { get; set; }
        public bool? IncludeScreenshot { get; set; }
        public int? Attempt { get; set; }
    }

    private sealed class AgentAction
    {
        public string? Key { get; set; }
        public string? Label { get; set; }
        public string? Value { get; set; }
    }

    private sealed class UnmappedMessage
    {
        public string? Label { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class ReviewMessage
    {
        public string? Message { get; set; }
        public string? Image { get; set; }
    }
}

public sealed record AgentPage(
    IReadOnlyList<FormField> Fields,
    IReadOnlyList<string> Buttons,
    string Screenshot,
    string Url = "",
    string Wall = "",
    string Surface = "");

public sealed record ExperienceJob(
    string? Title,
    string? Company,
    string? Location,
    string? StartMonth,
    string? StartYear,
    string? EndMonth,
    string? EndYear,
    bool Current,
    string? Description);

public sealed record FormToolCall(
    string Tool,
    string FieldId,
    string Value,
    bool Checked,
    int X = 0,
    int Y = 0,
    IReadOnlyList<ExperienceJob>? Jobs = null);
