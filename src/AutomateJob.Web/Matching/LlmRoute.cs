namespace AutomateJob.Web.Matching;

public static class LlmRoute
{
    public const string GeminiDefaultModel = "gemini-2.5-flash";

    public const string GeminiDefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai";

    public static bool OpenAiEnabled()
    {
        return ParseEnabled(Environment.GetEnvironmentVariable("OPENAI_ENABLED"), fallback: true);
    }

    public static bool AgentOpenAiEnabled()
    {
        return ParseEnabled(AutomateJob.Web.Jobs.AgentEnvFile.Value("OPENAI_ENABLED"), fallback: true);
    }

    public static bool CanScore(string? openAiKey)
    {
        if (!OpenAiEnabled())
        {
            return HasGeminiKey();
        }

        return !string.IsNullOrWhiteSpace(openAiKey) && openAiKey.Trim().StartsWith("sk-", StringComparison.Ordinal);
    }

    public static bool HasGeminiKey()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));
    }

    public static string MissingScoreKeyMessage()
    {
        return OpenAiEnabled()
            ? "Paste an OpenAI key that starts with sk- above. It is kept for this browser session only."
            : "OpenAI is off. Put a Gemini API key in GEMINI_API_KEY in the web .env file.";
    }

    public static bool ParseEnabled(string? raw, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Trim().ToLowerInvariant() is not ("false" or "0" or "off" or "no");
    }
}
