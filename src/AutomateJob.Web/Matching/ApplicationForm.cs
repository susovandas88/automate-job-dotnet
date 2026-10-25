using System.Text.Json;
using AutomateJob.Web.Jobs;

namespace AutomateJob.Web.Matching;

public sealed record FormField(
    string Label,
    string Kind,
    IReadOnlyList<string> Options,
    string Key = "",
    string Tag = "",
    string Placeholder = "",
    string Name = "",
    bool Required = false,
    string Group = "",
    string CurrentValue = "",
    bool Visual = false);

public sealed record FormAnswer(string Label, string? Value, string Key = "");

public static class ApplicationForm
{
    public const int MinimumScore = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool CanApply(int? score)
    {
        return score is > MinimumScore;
    }

    public static IReadOnlyList<FormAnswer> Parse(string modelText)
    {
        var text = modelText.Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("The model did not return application answers.");
        }

        var parsed = JsonSerializer.Deserialize<AnswerEnvelope>(text[start..(end + 1)], JsonOptions)
            ?? throw new InvalidOperationException("The model did not return application answers.");
        var answers = new List<FormAnswer>();
        foreach (var item in parsed.Answers ?? [])
        {
            var label = item.Label?.Trim() ?? "";
            if (label.Length == 0)
            {
                continue;
            }

            var value = string.IsNullOrWhiteSpace(item.Value) ? null : item.Value.Trim();
            answers.Add(new FormAnswer(label, value, item.Key?.Trim() ?? ""));
        }

        return answers;
    }

    public static IReadOnlyList<FormAnswer> KeepResumeFacts(IReadOnlyList<FormAnswer> answers, string resume)
    {
        var source = resume ?? "";
        var kept = new List<FormAnswer>();
        foreach (var answer in answers)
        {
            var label = answer.Label.Trim();
            if (label.Length == 0)
            {
                continue;
            }

            var value = answer.Value?.Trim();
            if (string.IsNullOrEmpty(value) || !source.Contains(value, StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(new FormAnswer(label, null, answer.Key));
                continue;
            }

            kept.Add(new FormAnswer(label, value, answer.Key));
        }

        return kept;
    }

    public static IReadOnlyList<FormAnswer> UseSavedDetails(
        IReadOnlyList<FormField> fields,
        IReadOnlyList<FormAnswer> answers,
        ApplicantProfile profile)
    {
        var email = FirstValue(profile.Email, answers, static label => IsEmail(label) && !IsRepeat(label));
        var filled = new List<FormAnswer>();
        foreach (var field in fields)
        {
            if (IsPassword(field.Label) || field.Kind.Equals("password", StringComparison.OrdinalIgnoreCase))
            {
                filled.Add(new FormAnswer(field.Label, null, field.Key));
                continue;
            }

            var saved = SavedValue(field.Label, profile, email);
            if (!string.IsNullOrWhiteSpace(saved))
            {
                filled.Add(new FormAnswer(field.Label, saved, field.Key));
                continue;
            }

            var existing = answers.FirstOrDefault(answer =>
                (!string.IsNullOrWhiteSpace(field.Key) && answer.Key == field.Key)
                || answer.Label.Equals(field.Label, StringComparison.OrdinalIgnoreCase));
            filled.Add(existing ?? new FormAnswer(field.Label, null, field.Key));
        }

        return filled;
    }

    private static string? SavedValue(string label, ApplicantProfile profile, string? email)
    {
        var text = label.Trim();
        if (IsRepeat(text) && IsEmail(text))
        {
            return email;
        }

        if (IsEmail(text))
        {
            return email;
        }

        if (Contains(text, "first name") || Contains(text, "given name"))
        {
            return FirstValue(profile.FirstName);
        }

        if (Contains(text, "last name") || Contains(text, "surname") || Contains(text, "family name"))
        {
            return FirstValue(profile.LastName);
        }

        if (Contains(text, "full name") || text.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            return FirstValue(string.Join(' ', new[] { profile.FirstName, profile.LastName }.Where(part => !string.IsNullOrWhiteSpace(part))));
        }

        if (Contains(text, "phone") || Contains(text, "mobile"))
        {
            return FirstValue(profile.Phone);
        }

        if (Contains(text, "city"))
        {
            return FirstValue(profile.City);
        }

        if (Contains(text, "country"))
        {
            return FirstValue(profile.Country);
        }

        if (Contains(text, "linkedin"))
        {
            return FirstValue(profile.LinkedInUrl);
        }

        if (Contains(text, "website") || Contains(text, "portfolio"))
        {
            return FirstValue(profile.Website);
        }

        if (Contains(text, "current title") || text.Equals("title", StringComparison.OrdinalIgnoreCase) || Contains(text, "job title"))
        {
            return FirstValue(profile.CurrentTitle);
        }

        if (Contains(text, "years"))
        {
            return FirstValue(profile.YearsOfExperience);
        }

        if (Contains(text, "authori"))
        {
            return FirstValue(profile.WorkAuthorization);
        }

        if (Contains(text, "notice"))
        {
            return FirstValue(profile.NoticePeriod);
        }

        if (Contains(text, "salary"))
        {
            return FirstValue(profile.ExpectedSalary);
        }

        if (Contains(text, "relocat"))
        {
            return FirstValue(profile.WillingToRelocate);
        }

        return null;
    }

    private static string? FirstValue(string? preferred, IReadOnlyList<FormAnswer>? answers = null, Func<string, bool>? match = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            return preferred.Trim();
        }

        if (answers is null || match is null)
        {
            return null;
        }

        return answers.FirstOrDefault(answer => !string.IsNullOrWhiteSpace(answer.Value) && match(answer.Label))?.Value?.Trim();
    }

    private static bool IsEmail(string label)
    {
        return Contains(label, "email") || Contains(label, "e-mail");
    }

    private static bool IsRepeat(string label)
    {
        return Contains(label, "retype") || Contains(label, "re-type") || Contains(label, "confirm") || Contains(label, "repeat") || Contains(label, "verify");
    }

    private static bool IsPassword(string label)
    {
        return Contains(label, "password") || Contains(label, "passcode");
    }

    private static bool Contains(string text, string part)
    {
        return text.Contains(part, StringComparison.OrdinalIgnoreCase);
    }

    public static string ChooseAction(IEnumerable<string> buttonTexts)
    {
        var texts = buttonTexts
            .Select(text => text.Trim())
            .Where(text => text.Length > 0)
            .ToList();
        if (texts.Any(IsSubmit))
        {
            return "submit";
        }

        if (texts.Any(IsAdvance))
        {
            return "advance";
        }

        return "stop";
    }

    public static bool IsSubmit(string text)
    {
        return text.Contains("submit", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdvance(string text)
    {
        var value = text.Trim();
        if (IsSubmit(value))
        {
            return false;
        }

        return value.StartsWith("Next", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Continue", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("Review", StringComparison.OrdinalIgnoreCase)
            || value.Contains("save and continue", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AnswerEnvelope
    {
        public List<AnswerItem>? Answers { get; set; }
    }

    private sealed class AnswerItem
    {
        public string? Label { get; set; }

        public string? Value { get; set; }

        public string? Key { get; set; }
    }
}
