using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutomateJob.Web.Matching;

public sealed record StoredResume(string Id, string Name, string SavedAt);

public sealed class ResumeLibrary
{
    private readonly string _folder;
    private readonly string _indexPath;
    private readonly object _gate = new();

    public ResumeLibrary(IWebHostEnvironment environment)
        : this(Path.Combine(AppHost.DataRoot(environment.ContentRootPath), "resumes"))
    {
    }

    internal ResumeLibrary(string folder)
    {
        _folder = folder;
        _indexPath = Path.Combine(folder, "index.json");
        Directory.CreateDirectory(folder);
    }

    public IReadOnlyList<StoredResume> List(long userId)
    {
        lock (_gate)
        {
            return ReadIndex()
                .Where(entry => entry.UserId == userId)
                .OrderByDescending(entry => entry.SavedAt, StringComparer.Ordinal)
                .Select(entry => new StoredResume(entry.Id, entry.Name, entry.SavedAt))
                .ToList();
        }
    }

    public void ClaimUnowned(long userId)
    {
        if (userId <= 0)
        {
            return;
        }

        lock (_gate)
        {
            var entries = ReadIndex();
            var changed = false;
            foreach (var entry in entries)
            {
                if (entry.UserId == 0)
                {
                    entry.UserId = userId;
                    changed = true;
                }
            }

            if (changed)
            {
                WriteIndex(entries);
            }
        }
    }

    public string Save(long userId, string name, string text)
    {
        var body = text.Trim();
        if (body.Length == 0)
        {
            throw new InvalidOperationException("The resume is empty.");
        }

        if (userId <= 0)
        {
            throw new InvalidOperationException("Sign in before saving a resume.");
        }

        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16].ToLowerInvariant();
        var displayName = CleanName(name);
        var savedAt = DateTimeOffset.UtcNow.ToString("O");

        lock (_gate)
        {
            File.WriteAllText(Path.Combine(_folder, id + ".txt"), body, Encoding.UTF8);
            var entries = ReadIndex();
            var existing = entries.FirstOrDefault(entry => entry.Id == id && entry.UserId == userId);
            if (existing is null)
            {
                entries.Add(new ResumeEntry { Id = id, UserId = userId, Name = displayName, SavedAt = savedAt });
            }
            else
            {
                existing.Name = displayName;
                existing.SavedAt = savedAt;
            }

            WriteIndex(entries);
        }

        return id;
    }

    public bool Delete(long userId, string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return false;
        }

        var key = id.Trim();
        lock (_gate)
        {
            var entries = ReadIndex();
            if (entries.RemoveAll(entry => entry.Id == key && entry.UserId == userId) == 0)
            {
                return false;
            }

            if (!entries.Any(entry => entry.Id == key))
            {
                var path = Path.Combine(_folder, key + ".txt");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            WriteIndex(entries);
            return true;
        }
    }

    public string? Read(long userId, string id)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(id) || id.Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            return null;
        }

        var key = id.Trim();
        lock (_gate)
        {
            if (!ReadIndex().Any(entry => entry.Id == key && entry.UserId == userId))
            {
                return null;
            }
        }

        var path = Path.Combine(_folder, key + ".txt");
        return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
    }

    public static string NameFromText(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "Pasted resume" : line;
    }

    private List<ResumeEntry> ReadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        var json = File.ReadAllText(_indexPath, Encoding.UTF8);
        return JsonSerializer.Deserialize<List<ResumeEntry>>(json) ?? [];
    }

    private void WriteIndex(List<ResumeEntry> entries)
    {
        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_indexPath, json, Encoding.UTF8);
    }

    private static string CleanName(string name)
    {
        var fileName = Path.GetFileName(name.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "Resume";
        }

        var cleaned = string.Join(" ", fileName.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= 80 ? cleaned : cleaned[..80];
    }

    private sealed class ResumeEntry
    {
        public string Id { get; set; } = "";

        public long UserId { get; set; }

        public string Name { get; set; } = "";

        public string SavedAt { get; set; } = "";
    }
}
