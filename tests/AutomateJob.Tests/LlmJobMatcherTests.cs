using System.Net;
using System.Text;
using AutomateJob.Web.Matching;
using Microsoft.Extensions.Options;

namespace AutomateJob.Tests;

public class LlmJobMatcherTests
{
    [Fact]
    public async Task Score_comes_from_the_model_response()
    {
        var handler = new StubHandler("""
            {
              "output": [
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"score\":82,\"summary\":\"The resume shows the backend work this role needs.\",\"matched\":[\"C# services\"],\"missing\":[],\"preferredMatched\":[],\"preferredMissing\":[\"Kubernetes\"]}"
                    }
                  ]
                }
              ]
            }
            """);
        var matcher = CreateMatcher(handler, apiKey: "sk-test");

        var report = await matcher.MatchAsync(
            "Built C# services for eight years.",
            "Requirements: design and run backend services for a product team that ships weekly.",
            "Backend engineer",
            CancellationToken.None);

        Assert.Equal(82, report.Score);
        Assert.Equal("Strong match", report.Verdict);
        Assert.Equal("gpt-4.1", report.ScoredBy);
        Assert.Contains("C# services", report.Matched);
        Assert.Contains("Kubernetes", report.PreferredMissing);
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());

        var body = handler.LastBody!;
        Assert.Contains("gpt-4.1", body);
        Assert.Contains("Built C# services", body);
        Assert.DoesNotContain("search_parameters", body);
    }

    [Fact]
    public async Task Evaluation_sends_token_usage_to_langsmith()
    {
        var previousFlag = Environment.GetEnvironmentVariable("OPENAI_ENABLED");
        Environment.SetEnvironmentVariable("OPENAI_ENABLED", "true");
        try
        {
            var handler = new StubHandler("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"score\":70,\"summary\":\"Partial fit.\",\"matched\":[\"C#\"],\"missing\":[],\"preferredMatched\":[],\"preferredMissing\":[]}"
                      }
                    }
                  ],
                  "usage": { "prompt_tokens": 120, "completion_tokens": 40, "total_tokens": 160 }
                }
                """);
            var matcher = CreateMatcher(handler, apiKey: "sk-test");

            var report = await matcher.MatchAsync(
                "Built C# services for eight years.",
                "Requirements: design and run backend services for a product team that ships weekly.",
                "Backend engineer",
                CancellationToken.None,
                "sk-test",
                "lsv2_test");

            Assert.Equal(70, report.Score);
            Assert.Equal("https://api.smith.langchain.com/runs", handler.LastRequest!.RequestUri!.ToString());
            Assert.Contains("input_tokens", handler.LastBody);
            Assert.Contains("120", handler.LastBody);
            Assert.Contains("40", handler.LastBody);
            Assert.Contains("160", handler.LastBody);
            Assert.Contains("Evaluate Backend engineer", handler.LastBody);
            Assert.Contains("dotted_order", handler.LastBody);
            Assert.DoesNotContain("lsv2_test", handler.LastBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_ENABLED", previousFlag);
        }
    }

    [Fact]
    public void Fenced_model_text_still_parses()
    {
        var score = LlmJobMatcher.ParseScore("""
            ```json
            {"score":140,"summary":"Partial overlap.","matched":["Excel"],"missing":["Python"],"preferredMatched":[],"preferredMissing":[]}
            ```
            """);

        Assert.Equal(100, score.Score);
        Assert.Equal("Partial overlap.", score.Summary);
    }

    [Fact]
    public async Task Missing_api_key_does_not_call_the_model()
    {
        var handler = new StubHandler("{}");
        var matcher = CreateMatcher(handler, apiKey: "  ");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.MatchAsync(
            "Resume text that is long enough to send.",
            "Job description that is long enough to score against a resume.",
            null,
            CancellationToken.None));

        Assert.Contains("platform.openai.com/api-keys", error.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task String_error_from_xai_is_shown_as_the_message()
    {
        var handler = new StubHandler(
            """{"code":"invalid-argument","error":"Incorrect API key provided. You can obtain an API key from https://console.x.ai."}""",
            HttpStatusCode.BadRequest);
        var matcher = CreateMatcher(handler, apiKey: "sk-not-valid");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.MatchAsync(
            "Resume text that is long enough to send.",
            "Job description that is long enough to score against a resume.",
            null,
            CancellationToken.None));

        Assert.Contains("starts with sk-", error.Message);
        Assert.DoesNotContain("element of type", error.Message);
    }

    [Fact]
    public async Task Cursor_key_is_rejected_before_the_request()
    {
        var handler = new StubHandler("{}");
        var matcher = CreateMatcher(handler, apiKey: "crsr_example");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.MatchAsync(
            "Resume text that is long enough to send.",
            "Job description that is long enough to score against a resume.",
            null,
            CancellationToken.None));

        Assert.Contains("Cursor key", error.Message);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Gemini_is_used_when_openai_is_off()
    {
        var previousFlag = Environment.GetEnvironmentVariable("OPENAI_ENABLED");
        var previousKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var previousModel = Environment.GetEnvironmentVariable("GEMINI_MODEL");
        var previousBase = Environment.GetEnvironmentVariable("GEMINI_BASE_URL");
        Environment.SetEnvironmentVariable("OPENAI_ENABLED", "false");
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "AIza-test");
        Environment.SetEnvironmentVariable("GEMINI_MODEL", "gemini-2.5-flash");
        Environment.SetEnvironmentVariable("GEMINI_BASE_URL", "https://generativelanguage.googleapis.com/v1beta/openai");
        try
        {
            var handler = new StubHandler("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "Score: 70%\nReasoning: Partial fit on backend services, with a gap in seniority evidence.\nCovered: C# services; Azure\nMissing: Kafka"
                      }
                    }
                  ]
                }
                """);
            var matcher = CreateMatcher(handler, apiKey: "sk-should-not-be-sent");

            var report = await matcher.MatchAsync(
                "Built C# services for eight years.",
                "Requirements: design and run backend services for a product team that ships weekly.",
                "Backend engineer",
                CancellationToken.None);

            Assert.Equal(70, report.Score);
            Assert.Contains("Partial fit on backend services", report.Summary);
            Assert.Equal(["C# services", "Azure"], report.Matched);
            Assert.Equal(["Kafka"], report.Missing);
            Assert.Equal("gemini-2.5-flash", report.ScoredBy);
            Assert.Contains("Technical Skills Match", handler.LastBody);
            Assert.DoesNotContain("json_object", handler.LastBody);
            Assert.Equal("https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", handler.LastRequest!.RequestUri!.ToString());
            Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
            Assert.Equal("AIza-test", handler.LastRequest.Headers.Authorization.Parameter);
            Assert.Contains("gemini-2.5-flash", handler.LastBody);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_ENABLED", previousFlag);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("GEMINI_MODEL", previousModel);
            Environment.SetEnvironmentVariable("GEMINI_BASE_URL", previousBase);
        }
    }

    [Fact]
    public async Task Rejected_key_returns_a_clear_error()
    {
        var handler = new StubHandler("{}", HttpStatusCode.Unauthorized);
        var matcher = CreateMatcher(handler, apiKey: "sk-bad");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.MatchAsync(
            "Resume text that is long enough to send.",
            "Job description that is long enough to score against a resume.",
            null,
            CancellationToken.None));

        Assert.Contains("OpenAI rejected this API key", error.Message);
    }

    private static LlmJobMatcher CreateMatcher(StubHandler handler, string? apiKey)
    {
        var http = new HttpClient(handler);
        var options = Options.Create(new LlmOptions { ApiKey = apiKey });
        return new LlmJobMatcher(http, options);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
