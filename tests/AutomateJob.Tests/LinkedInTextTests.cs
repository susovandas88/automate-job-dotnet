using AutomateJob.Web.Jobs;

namespace AutomateJob.Tests;

public class LinkedInTextTests
{
    [Fact]
    public void Location_keeps_the_place_and_drops_posting_meta()
    {
        var location = LinkedInText.Location("Bengaluru, Karnataka, India · 2 days ago · 48 applicants");

        Assert.Equal("Bengaluru, Karnataka, India", location);
    }

    [Fact]
    public void Form_fields_parse_when_playwright_returns_json()
    {
        var fields = LinkedInSession.ParseFields("""
            [{"key":"0","label":"Phone","kind":"text","options":[]},{"key":"1","label":"Work authorization","kind":"radio","options":["Yes","No"]}]
            """);

        Assert.Equal(2, fields.Count);
        Assert.Equal("Phone", fields[0].Label);
        Assert.Equal("0", fields[0].Key);
        Assert.Equal(["Yes", "No"], fields[1].Options);
    }

    [Fact]
    public void Form_fields_keep_every_clue_for_the_model()
    {
        var fields = LinkedInSession.ParseFields("""
            [{"key":"0","tag":"input","kind":"email","label":"Email Address","placeholder":"name@company.com","name":"email","required":true,"group":"Sign in","options":[],"currentValue":""}]
            """);

        var field = Assert.Single(fields);
        Assert.Equal("Email Address", field.Label);
        Assert.Equal("email", field.Kind);
        Assert.Equal("input", field.Tag);
        Assert.Equal("name@company.com", field.Placeholder);
        Assert.Equal("email", field.Name);
        Assert.True(field.Required);
        Assert.Equal("Sign in", field.Group);
        Assert.False(field.Visual);
    }

    [Fact]
    public void Form_fields_keep_a_visual_control_and_a_frame_id()
    {
        var fields = LinkedInSession.ParseFields("""
            [{"key":"f1e0","tag":"input","kind":"text","label":"","placeholder":"","name":"","required":false,"group":"","options":[],"currentValue":""}]
            """);

        var field = Assert.Single(fields);
        Assert.Equal("f1e0", field.Key);
        Assert.True(field.Visual);
    }

    [Fact]
    public void Title_uses_the_first_line()
    {
        var title = LinkedInText.Title("Senior Software Engineer\nwith verification");

        Assert.Equal("Senior Software Engineer", title);
    }

    [Fact]
    public async Task Form_tools_refuse_submit_and_do_not_take_a_file_path()
    {
        var submit = await LinkedInSession.PerformFormToolAsync(
            null!,
            new FormToolCall("next_or_submit", "", "Submit", false),
            CancellationToken.None);
        var apple = await LinkedInSession.PerformFormToolAsync(
            null!,
            new FormToolCall("next_or_submit", "", "Sign in with Apple", false),
            CancellationToken.None);
        var upload = await LinkedInSession.PerformFormToolAsync(
            null!,
            new FormToolCall("upload_file", "0", "resume", false),
            CancellationToken.None);
        var path = await LinkedInSession.PerformFormToolAsync(
            null!,
            new FormToolCall("upload_file", "0", "C:\\secret\\resume.pdf", false),
            CancellationToken.None);

        Assert.Contains("Refused", submit);
        Assert.Contains("Refused", apple);
        Assert.Contains("not stored", upload);
        Assert.Contains("must be resume or cover_letter", path);
    }

    [Fact]
    public void Answers_bank_keeps_a_manual_answer_and_reuses_the_same_question()
    {
        var bank = new AnswersBank(Path.Combine(Path.GetTempPath(), "answers-bank-" + Guid.NewGuid().ToString("N") + ".json"));
        bank.Remember("Years of Python?", "");
        bank.Remember("Years of Python?", "5");
        bank.Remember("Years of Python", "");

        var saved = Assert.Single(bank.Answered());
        Assert.Equal("5", saved.Answer);
        Assert.Empty(bank.Pending());
        Assert.Equal(AnswersBank.Normalize("Years of Python?"), AnswersBank.Normalize("Years of Python"));
    }

    [Fact]
    public void Pace_does_not_block_after_many_applications()
    {
        var pace = new ApplicationPace(Path.Combine(Path.GetTempPath(), "pace-" + Guid.NewGuid().ToString("N") + ".json"));
        for (var count = 0; count < 12; count++)
        {
            Assert.Null(pace.BlockReason());
            pace.MarkStarted();
        }

        Assert.Null(pace.BlockReason());
    }

    [Fact]
    public void Company_routes_leave_submit_and_email_with_the_person()
    {
        Assert.Contains("you click Submit", ApplicationRoute.Hint("https://boards.greenhouse.io/acme/jobs/1"));
        Assert.Contains("you click Submit", ApplicationRoute.Hint("https://jobs.lever.co/acme/123"));
        Assert.Contains("you click Submit", ApplicationRoute.Hint("https://jobs.ashbyhq.com/acme/123"));
        Assert.Contains("does not send mail", ApplicationRoute.Hint("mailto:jobs@acme.com"));
    }
}
