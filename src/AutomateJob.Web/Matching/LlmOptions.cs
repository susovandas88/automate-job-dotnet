namespace AutomateJob.Web.Matching;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    public string Model { get; set; } = "gpt-4.1";

    public string? ApiKey { get; set; }

    public bool OpenAiEnabled { get; set; } = true;

    public string GeminiBaseUrl { get; set; } = LlmRoute.GeminiDefaultBaseUrl;

    public string GeminiModel { get; set; } = LlmRoute.GeminiDefaultModel;

    public string? GeminiApiKey { get; set; } 
}
