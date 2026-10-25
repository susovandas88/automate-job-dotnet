using System.Text.Json;

namespace AutomateJob.Web.Jobs;

public sealed record SavedAnswer(string Question, string Answer);

public sealed class AnswersBank
{
    private readonly string _path;
    private readonly object _gate = new();

    public AnswersBank(IWebHostEnvironment environment)
        : this(Path.Combine(AppHost.DataRoot(environment.ContentRootPath), "answers-bank.json"))
    {
    }

    internal AnswersBank(string path)
    {
        _path = path;
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
    }

    public IReadOnlyList<SavedAnswer> Answered()
    {
        return List().Where(item => !string.IsNullOrWhiteSpace(item.Answer)).ToList();
    }

    public IReadOnlyList<SavedAnswer> Pending()
    {
        return List().Where(item => string.IsNullOrWhiteSpace(item.Answer)).ToList();
    }

    public void Remember(string question, string answer)
    {
        var text = (question ?? "").Trim();
        if (text.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            var items = Read();
            var key = Normalize(text);
            var index = items.FindIndex(item => Normalize(item.Question) == key);
            var next = new SavedAnswer(text, (answer ?? "").Trim());
            if (index >= 0)
            {
                if (string.IsNullOrWhiteSpace(next.Answer) && !string.IsNullOrWhiteSpace(items[index].Answer))
                {
                    return;
                }

                items[index] = next;
            }
            else
            {
                items.Add(next);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(items));
        }
    }

    public static string Normalize(string question)
    {
        var characters = (question ?? "").ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray();
        return string.Join(' ', new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private List<SavedAnswer> List()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    private List<SavedAnswer> Read()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SavedAnswer>>(File.ReadAllText(_path)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
