using AutomateJob.Web.Jobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AutomateJob.Web.Pages;

public class UnmappedModel : PageModel
{
    private readonly AnswersBank _answers;

    public UnmappedModel(AnswersBank answers)
    {
        _answers = answers;
    }

    public IReadOnlyList<SavedAnswer> PendingAnswers { get; private set; } = [];

    public int SavedAnswerCount { get; private set; }

    public string? Message { get; private set; }

    public void OnGet()
    {
        Load();
    }

    public IActionResult OnPost(string[]? bankQuestion, string[]? bankAnswer)
    {
        var questions = bankQuestion ?? [];
        var answers = bankAnswer ?? [];
        var saved = 0;
        for (var index = 0; index < questions.Length; index++)
        {
            var answer = index < answers.Length ? answers[index] : "";
            if (string.IsNullOrWhiteSpace(answer))
            {
                continue;
            }

            _answers.Remember(questions[index], answer);
            saved++;
        }

        Message = saved == 0
            ? "Type an answer for each question you want saved."
            : "Saved " + saved + " answer" + (saved == 1 ? "" : "s") + ". The next fill uses them when the same question appears.";
        Load();
        return Page();
    }

    private void Load()
    {
        PendingAnswers = _answers.Pending();
        SavedAnswerCount = _answers.Answered().Count;
    }
}
