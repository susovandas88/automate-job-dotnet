using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AutomateJob.Web.Matching;

public sealed class LlmJobMatcher
{
    private const int MaxInputCharacters = 30_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public LlmJobMatcher(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<MatchReport> MatchAsync(
        string resume,
        string jobDescription,
        string? jobTitle,
        CancellationToken cancellationToken,
        string? apiKey = null,
        string? langsmithApiKey = null)
    {
        var openAi = UseOpenAi();
        var started = DateTimeOffset.UtcNow;
        var system = openAi
            ? """
            You score how well one resume fits one job. Read both texts and judge the fit from relevant experience, seniority, domain, responsibilities, and required skills. Demonstrated work counts more than a skill listed once with no evidence. Count closely related experience when it clearly transfers. Ignore benefits, legal boilerplate, and company marketing. Return only a JSON object with integer score from 0 to 100, summary, matched, missing, preferredMatched, and preferredMissing. Use 80-100 for a strong fit, 55-79 for a partial fit, and 0-54 for a weak fit. Lists contain short phrases. Use empty arrays when a list has no items.
            """
            : "Follow the requested output format exactly. Do not add any other sections.";
        var user = openAi ? BuildInput(resume, jobDescription, jobTitle) : BuildRecruiterInput(resume, jobDescription, jobTitle);
        var body = await PostChatAsync(
            ResolveApiKey(apiKey),
            system,
            user,
            cancellationToken,
            jsonObject: openAi);
        var modelText = ReadModelText(body);
        await TraceEvaluationAsync(langsmithApiKey, jobTitle, system, user, modelText, body, started);
        var score = openAi ? ParseScore(modelText) : ParseRecruiterScore(modelText);
        var verdict = score.Score >= 80 ? "Strong match" : score.Score >= 55 ? "Partial match" : "Weak match";
        var summary = string.IsNullOrWhiteSpace(score.Summary)
            ? $"The model scored this resume {score.Score} out of 100 for the role."
            : score.Summary.Trim();

        return new MatchReport(
            score.Score,
            verdict,
            summary,
            Clean(score.Matched),
            Clean(score.Missing),
            Clean(score.PreferredMatched),
            Clean(score.PreferredMissing),
            ModelName());
    }

    public async Task<SearchSuggestion> SuggestSearchAsync(
        string resume,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        var body = await PostChatAsync(
            ResolveApiKey(apiKey),
            """
            Read a resume and propose one LinkedIn job search. Return only a JSON object with keywords and location. keywords is a short role title plus the main skills, suitable for a job search box. location is a city or region stated in the resume, or an empty string when none is stated.
            """,
            resume.Trim(),
            cancellationToken);
        return ParseSearchSuggestion(ReadModelText(body));
    }

    public async Task<IReadOnlyList<FormAnswer>> MapApplicationAsync(
        string resume,
        IReadOnlyList<FormField> fields,
        CancellationToken cancellationToken,
        string? apiKey = null)
    {
        var listed = string.Join(
            "\n",
            fields.Take(40).Select(field =>
            {
                var options = field.Options.Count == 0 ? "" : " Options: " + string.Join(" | ", field.Options);
                return "- key " + field.Key + ": " + field.Label + " (" + field.Kind + ")" + options;
            }));
        var body = await PostChatAsync(
            ResolveApiKey(apiKey),
            """
            You fill one job application from a resume and saved application details in that same text. Return only a JSON object with answers, an array of objects with key, label, and value. Copy key from the field list. Copy value from the text. When a field asks to retype or confirm an email, repeat that email. Use null when the text does not state the answer. Do not invent phone numbers, emails, years, visa status, salary, dates, or passwords.
            """,
            $"""
            Resume:
            {Limit(resume)}

            Fields:
            {listed}
            """,
            cancellationToken,
            2000);
        return ApplicationForm.KeepResumeFacts(ApplicationForm.Parse(ReadModelText(body)), resume);
    }

    private async Task<string> PostChatAsync(
        string apiKey,
        string system,
        string user,
        CancellationToken cancellationToken,
        int maxTokens = 1200,
        bool jsonObject = true)
    {
        var openAi = UseOpenAi();
        var model = ModelName();
        var endpoint = new Uri(new Uri(BaseUrl()), "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        var messages = new object[]
        {
            new { role = "system", content = system },
            new { role = "user", content = user }
        };
        request.Content = JsonContent.Create(openAi
            ? new
            {
                model,
                temperature = 0.2,
                max_completion_tokens = maxTokens,
                response_format = new { type = "json_object" },
                messages
            }
            : jsonObject
                ? (object)new
                {
                    model,
                    temperature = 0.2,
                    max_tokens = maxTokens,
                    response_format = new { type = "json_object" },
                    messages
                }
                : new
                {
                    model,
                    temperature = 0.2,
                    max_tokens = maxTokens,
                    messages
                });

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(openAi
                ? "OpenAI rejected this API key. Create one at https://platform.openai.com/api-keys and paste it. It starts with sk-."
                : "Gemini rejected this API key. Create one at https://aistudio.google.com/apikey and put it in GEMINI_API_KEY.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = ReadError(body);
            if (detail.Contains("Incorrect API key", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("invalid_api_key", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("API key not valid", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(UseOpenAi()
                    ? "OpenAI rejected this API key. Create one at https://platform.openai.com/api-keys and paste it. It starts with sk-."
                    : "Gemini rejected this API key. Create one at https://aistudio.google.com/apikey and put it in GEMINI_API_KEY.");
            }

            throw new InvalidOperationException($"The model request failed ({(int)response.StatusCode}). {detail}".Trim());
        }

        return body;
    }

    private async Task TraceEvaluationAsync(
        string? langsmithApiKey,
        string? jobTitle,
        string system,
        string user,
        string modelText,
        string body,
        DateTimeOffset started)
    {
        var key = langsmithApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("lsv2_", StringComparison.Ordinal))
        {
            return;
        }

        var usage = ReadUsage(body);
        var id = Guid.NewGuid();
        var ended = DateTimeOffset.UtcNow;
        var model = ModelName();
        var payload = new
        {
            id,
            trace_id = id,
            dotted_order = started.UtcDateTime.ToString("yyyyMMdd'T'HHmmssffffff") + "Z" + id,
            name = string.IsNullOrWhiteSpace(jobTitle) ? "Evaluate job" : "Evaluate " + jobTitle.Trim(),
            run_type = "llm",
            start_time = started.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            end_time = ended.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"),
            session_name = "automate-job",
            inputs = new
            {
                messages = new object[]
                {
                    new { role = "system", content = Limit(system, 2000) },
                    new { role = "user", content = Limit(user, 4000) }
                }
            },
            outputs = new
            {
                choices = new[] { new { message = new { role = "assistant", content = Limit(modelText, 2000) } } },
                llm_output = new
                {
                    token_usage = new
                    {
                        prompt_tokens = usage.Input,
                        completion_tokens = usage.Output,
                        total_tokens = usage.Total
                    }
                }
            },
            usage_metadata = new
            {
                input_tokens = usage.Input,
                output_tokens = usage.Output,
                total_tokens = usage.Total
            },
            extra = new
            {
                metadata = new
                {
                    ls_model_name = model,
                    ls_provider = UseOpenAi() ? "openai" : "google"
                }
            }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.smith.langchain.com/runs");
            request.Headers.TryAddWithoutValidation("x-api-key", key);
            request.Content = JsonContent.Create(payload);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var response = await _http.SendAsync(request, timeout.Token);
        }
        catch (Exception)
        {
        }
    }

    private readonly record struct TokenUse(int Input, int Output, int Total);

    private static TokenUse ReadUsage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return new TokenUse(0, 0, 0);
            }

            var input = ReadToken(usage, "prompt_tokens", "input_tokens");
            var output = ReadToken(usage, "completion_tokens", "output_tokens");
            var total = ReadToken(usage, "total_tokens");
            if (total == 0)
            {
                total = input + output;
            }

            return new TokenUse(input, output, total);
        }
        catch (JsonException)
        {
            return new TokenUse(0, 0, 0);
        }
    }

    private static int ReadToken(JsonElement usage, params string[] names)
    {
        foreach (var name in names)
        {
            if (usage.TryGetProperty(name, out var value) && value.TryGetInt32(out var count))
            {
                return count;
            }
        }

        return 0;
    }

    private bool UseOpenAi()
    {
        var fromEnv = Environment.GetEnvironmentVariable("OPENAI_ENABLED");
        return LlmRoute.ParseEnabled(fromEnv, _options.OpenAiEnabled);
    }

    private string ModelName()
    {
        if (!UseOpenAi())
        {
            var geminiModel = Environment.GetEnvironmentVariable("GEMINI_MODEL");
            if (!string.IsNullOrWhiteSpace(geminiModel))
            {
                return geminiModel.Trim();
            }

            return string.IsNullOrWhiteSpace(_options.GeminiModel) ? LlmRoute.GeminiDefaultModel : _options.GeminiModel.Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv.Trim();
        }

        return string.IsNullOrWhiteSpace(_options.Model) ? "gpt-4.1" : _options.Model.Trim();
    }

    private string BaseUrl()
    {
        if (!UseOpenAi())
        {
            var geminiBase = Environment.GetEnvironmentVariable("GEMINI_BASE_URL");
            return NormalizeBaseUrl(string.IsNullOrWhiteSpace(geminiBase) ? _options.GeminiBaseUrl : geminiBase, LlmRoute.GeminiDefaultBaseUrl);
        }

        var fromEnv = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        return NormalizeBaseUrl(string.IsNullOrWhiteSpace(fromEnv) ? _options.BaseUrl : fromEnv);
    }

    internal static SearchSuggestion ParseSearchSuggestion(string modelText)
    {
        var json = ExtractJson(modelText);
        var parsed = JsonSerializer.Deserialize<SearchSuggestion>(json, JsonOptions)
            ?? throw new InvalidOperationException("The model did not return a job search.");
        if (string.IsNullOrWhiteSpace(parsed.Keywords))
        {
            throw new InvalidOperationException("The model did not return search keywords.");
        }

        return parsed with
        {
            Keywords = parsed.Keywords.Trim(),
            Location = parsed.Location?.Trim() ?? ""
        };
    }

    private string ResolveApiKey(string? apiKey)
    {
        if (!UseOpenAi())
        {
            var gemini = FirstKey(Environment.GetEnvironmentVariable("GEMINI_API_KEY"), _options.GeminiApiKey);
            if (gemini is null)
            {
                throw new InvalidOperationException(
                    "OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in the web .env file.");
            }

            return gemini;
        }

        var resolved = FirstKey(apiKey, _options.ApiKey);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                "Paste an OpenAI API key above. It must start with sk-. Create one at https://platform.openai.com/api-keys.");
        }

        if (resolved.StartsWith("crsr_", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That value is a Cursor key. Paste an OpenAI key from https://platform.openai.com/api-keys. It starts with sk-.");
        }

        if (resolved.StartsWith("xai-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "That value is an xAI key. Paste an OpenAI key from https://platform.openai.com/api-keys. It starts with sk-.");
        }

        if (!resolved.StartsWith("sk-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "That value is not an OpenAI API key. Create one at https://platform.openai.com/api-keys. It starts with sk-.");
        }

        return resolved;
    }

    private static string? FirstKey(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var key = NormalizeKey(candidate);
            if (key is not null)
            {
                return key;
            }
        }

        return null;
    }

    private static string? NormalizeKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var key = apiKey.Trim().Trim('"').Trim('\'').Trim();
        const string bearer = "Bearer ";
        if (key.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            key = key[bearer.Length..].Trim();
        }

        return key.Length == 0 ? null : key;
    }

    private static string BuildInput(string resume, string jobDescription, string? jobTitle)
    {
        var title = string.IsNullOrWhiteSpace(jobTitle) ? "Not provided" : jobTitle.Trim();
        return $"""
            Job title:
            {title}

            Job description:
            {Limit(jobDescription)}

            Resume:
            {Limit(resume)}
            """;
    }

    private static string Limit(string value, int max = MaxInputCharacters)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        return trimmed[..max] + "\n[truncated]";
    }

    internal static string ReadModelText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("content", out var content))
                {
                    continue;
                }

                AppendContent(builder, content);
            }

            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].ValueKind == JsonValueKind.Object
            && choices[0].TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var messageContent))
        {
            var builder = new StringBuilder();
            AppendContent(builder, messageContent);
            if (builder.Length > 0)
            {
                return builder.ToString();
            }
        }

        throw new InvalidOperationException("The model response did not include a score.");
    }

    internal static LlmScore ParseRecruiterScore(string modelText)
    {
        var match = Regex.Match(modelText ?? "", @"Score:\s*(\d{1,3})\s*%?", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var score))
        {
            throw new InvalidOperationException("The model did not return a score percentage.");
        }

        var text = modelText ?? "";
        var summary = Section(text, "Reasoning");
        return new LlmScore
        {
            Score = Math.Clamp(score, 0, 100),
            Summary = string.IsNullOrWhiteSpace(summary) ? null : summary,
            Matched = Phrases(Section(text, "Covered")),
            Missing = Phrases(Section(text, "Missing"))
        };
    }

    private static string Section(string text, string label)
    {
        var match = Regex.Match(text, $@"{label}\s*:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return "";
        }

        var value = match.Groups[1].Value;
        var next = Regex.Match(value, @"\r?\n\s*(?:Score|Reasoning|Covered|Missing)\s*:", RegexOptions.IgnoreCase);
        if (next.Success)
        {
            value = value[..next.Index];
        }

        return value.Trim();
    }

    private static List<string> Phrases(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("None", StringComparison.OrdinalIgnoreCase) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return value
            .Split([';', '\n', '•'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim().TrimStart('-', '*').Trim())
            .Where(item => item.Length > 0 && !item.Equals("None", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string BuildRecruiterInput(string resume, string jobDescription, string? jobTitle)
    {
        var description = string.IsNullOrWhiteSpace(jobTitle)
            ? Limit(jobDescription)
            : jobTitle.Trim() + "\n" + Limit(jobDescription);
        return $"""
            Act as an expert HR recruiter. Compare the Resume provided below against the Job Description (JD).

            Evaluate the candidate based on 3 main criteria:
            1. Technical Skills Match (Weight: 40%)
            2. Experience & Seniority Level Match (Weight: 40%)
            3. Education & Certifications Match (Weight: 20%)

            Calculate a final overall Match Score from 0 to 100%. Provide the output strictly in this format:
            Score: [Insert Calculated Percentage]%
            Reasoning: [1-2 sentences explaining the gaps or strengths]
            Covered: [skills and experience from the resume that match the job, separated by semicolons]
            Missing: [required skills or experience the resume does not show, separated by semicolons]

            Use short phrases in Covered and Missing, the same kind of list used for a match table. Write None when a list has no items.

            ---
            {description}
            ---
            {Limit(resume)}
            """;
    }

    internal static LlmScore ParseScore(string modelText)
    {
        var json = ExtractJson(modelText);
        var parsed = JsonSerializer.Deserialize<LlmScore>(json, JsonOptions)
            ?? throw new InvalidOperationException("The model response could not be read as a score.");

        parsed.Score = Math.Clamp(parsed.Score, 0, 100);
        return parsed;
    }

    private static string ExtractJson(string modelText)
    {
        var text = modelText.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                text = text[(firstLine + 1)..lastFence].Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The model did not return a score in JSON.");
        }

        return text[start..(end + 1)];
    }

    private static void AppendContent(StringBuilder builder, JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            builder.Append(content.GetString());
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                builder.Append(part.GetString());
                continue;
            }

            if (part.ValueKind == JsonValueKind.Object
                && part.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                builder.Append(text.GetString());
            }
        }
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error))
            {
                return "";
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return TrimError(error.GetString());
            }

            if (error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return TrimError(message.GetString());
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    private static string TrimError(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        return text.Length <= 240 ? text : text[..240];
    }

    private static string NormalizeBaseUrl(string? baseUrl, string fallback = "https://api.openai.com/v1")
    {
        var value = string.IsNullOrWhiteSpace(baseUrl) ? fallback : baseUrl.Trim();
        return value.EndsWith('/') ? value : value + "/";
    }

    private static IReadOnlyList<string> Clean(IEnumerable<string>? items)
    {
        if (items is null)
        {
            return [];
        }

        return items
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    internal sealed class LlmScore
    {
        public int Score { get; set; }

        public string? Summary { get; set; }

        public List<string>? Matched { get; set; }

        public List<string>? Missing { get; set; }

        [JsonPropertyName("preferredMatched")]
        public List<string>? PreferredMatched { get; set; }

        [JsonPropertyName("preferredMissing")]
        public List<string>? PreferredMissing { get; set; }
    }
}
