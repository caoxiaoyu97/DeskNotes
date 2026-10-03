using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskNotes.Core;

public sealed class TaskItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class MarkdownConflictException(string message) : IOException(message);

public sealed class MarkdownStore
{
    private readonly string root;
    private readonly Dictionary<string, Entry> baseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset?> invalidatedCompletion = new(StringComparer.Ordinal);
    private sealed record Entry(string Path, int Start, int Length, string Raw, TaskItem Item);
    private sealed record Metadata(string id, DateTimeOffset created, DateTimeOffset? completed, DateTimeOffset? deleted, bool isCompleted);
    private static readonly Regex Lines = new(@"[^\r\n]*(?:\r\n|\n|\r|$)");
    private static readonly Regex TaskLine = new(@"^- \[([ xX])\] (.*)$");
    private const string Prefix = "<!-- desknote:";
    private const string End = "<!-- /desknote -->";

    public MarkdownStore(string root) => this.root = Path.GetFullPath(root);

    public List<TaskItem> Load()
    {
        var entries = ReadAll();
        baseline.Clear();
        foreach (var entry in entries) baseline.Add(entry.Item.Id, entry);
        return entries.Select(e => e.Item).ToList();
    }

    public void Save(TaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(item.Id)) throw new ArgumentException("Task id must not be empty.");
        Directory.CreateDirectory(Path.Combine(root, "records"));
        using var storeLock = new FileStream(Path.Combine(root, "records", ".desknote.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = ReadAll();
        var current = entries.SingleOrDefault(e => e.Item.Id == item.Id);
        baseline.TryGetValue(item.Id, out var prior);
        if (prior is not null && (current is null || current.Path != prior.Path || current.Raw != prior.Raw))
            throw new MarkdownConflictException("Task was changed or removed externally. Reload before saving.");
        if (prior is null && current is not null)
            throw new MarkdownConflictException("Task already exists. Load before saving.");
        if (current is not null && item.CreatedAt != current.Item.CreatedAt)
            throw new MarkdownConflictException("CreatedAt cannot be changed for an existing task.");
        string path = current?.Path ?? Path.Combine(root, "records", item.CreatedAt.ToLocalTime().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ".md");
        byte[]? before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        string text = before is null ? "" : Decode(before);
        // Re-parse the exact snapshot to avoid applying offsets from an earlier read.
        var snapshot = Parse(path, text).SingleOrDefault(e => e.Item.Id == item.Id);
        if ((current is null && snapshot is not null) || (current is not null && (snapshot is null || snapshot.Raw != current.Raw)))
            throw new MarkdownConflictException("Task changed while saving.");
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string block = Serialize(item, newline);
        string updated = snapshot is null
            ? AppendToDay(text, item, block, newline)
            : text[..snapshot.Start] + block + text[(snapshot.Start + snapshot.Length)..];
        var encoding = new UTF8Encoding(false, true);
        byte[] payload = encoding.GetBytes(updated);
        if (before is not null && before.AsSpan().StartsWith(new byte[] { 239, 187, 191 }))
            payload = new byte[] { 239, 187, 191 }.Concat(payload).ToArray();
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(payload); stream.Flush(true); }
            if (before is null ? File.Exists(path) : !File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(before))
                throw new MarkdownConflictException("File changed while saving. Retry after reloading.");
            if (before is null) File.Move(temp, path);
            else
            {
                string backups = Path.Combine(root, "backups");
                Directory.CreateDirectory(backups);
                string backup = Path.Combine(backups, Path.GetFileName(path) + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff") + "." + Guid.NewGuid().ToString("N") + ".bak");
                File.Replace(temp, path, backup);
            }
            invalidatedCompletion.Remove(item.Id);
            baseline[item.Id] = Parse(path, updated).Single(e => e.Item.Id == item.Id);
            // A successful commit remains successful even if another process holds an old backup open.
            PruneBackups(path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void PruneBackups(string path)
    {
        string directory = Path.Combine(root, "backups");
        if (!Directory.Exists(directory)) return;
        try
        {
            foreach (string backup in Directory.GetFiles(directory, Path.GetFileName(path) + ".*.bak").OrderDescending(StringComparer.Ordinal).Skip(50))
                try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string AppendToDay(string text, TaskItem item, string block, string nl)
    {
        string day = item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (text.Length == 0) text = "# " + day[..7] + nl + nl;
        var headings = Regex.Matches(text, @"(?m)^#{1,2} [^\r\n]*(?:\r\n|\n|\r|$)").Cast<Match>().ToArray();
        var heading = headings.FirstOrDefault(h => h.Value.TrimEnd('\r', '\n', ' ') == "## " + day);
        if (heading is not null)
        {
            int end = headings.FirstOrDefault(h => h.Index > heading.Index)?.Index ?? text.Length;
            string prefix = text[..end];
            return prefix + (prefix.EndsWith('\n') || prefix.EndsWith('\r') ? "" : nl) + block + nl + text[end..];
        }
        return text + (text.EndsWith('\n') || text.EndsWith('\r') ? "" : nl) + nl + "## " + day + nl + block;
    }

    private List<Entry> ReadAll()
    {
        var dir = Path.Combine(root, "records");
        var result = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md").Order(StringComparer.Ordinal).SelectMany(p => Parse(p, Decode(File.ReadAllBytes(p)))).ToList()
            : new List<Entry>();
        if (result.GroupBy(e => e.Item.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new MarkdownConflictException("Duplicate task ids found in Markdown records.");
        return result;
    }

    private static string Decode(byte[] bytes)
    {
        int offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    private IEnumerable<Entry> Parse(string path, string text)
    {
        var lines = Lines.Matches(text).Cast<Match>().Where(m => m.Length > 0).ToArray();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        DateOnly? headingDate = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            string value = line.Value.TrimEnd('\r', '\n');
            if (value.StartsWith("## ", StringComparison.Ordinal) && DateOnly.TryParseExact(value[3..].Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsedDay))
                headingDate = parsedDay;
            if (value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                int end = i + 1;
                while (end < lines.Length && lines[end].Value.TrimEnd('\r', '\n') != End) end++;
                if (!value.EndsWith(" -->", StringComparison.Ordinal) || end == lines.Length)
                    throw new InvalidDataException("Malformed DeskNotes metadata in " + path);
                TaskItem item;
                try
                {
                    string json = value[Prefix.Length..^4];
                    using var document = JsonDocument.Parse(json);
                    foreach (var name in new[] { "id", "created", "completed", "deleted", "isCompleted" })
                        if (!document.RootElement.TryGetProperty(name, out _)) throw new JsonException("Missing " + name);
                    var meta = JsonSerializer.Deserialize<Metadata>(json) ?? throw new JsonException();
                    item = new TaskItem { Id = meta.id, CreatedAt = meta.created, CompletedAt = meta.completed, DeletedAt = meta.deleted, IsCompleted = meta.isCompleted };
                }
                catch (Exception ex) when (ex is FormatException or JsonException) { throw new InvalidDataException("Invalid task metadata in " + path, ex); }
                if (string.IsNullOrWhiteSpace(item.Id)) throw new InvalidDataException("Missing task id.");
                // The rendered Markdown is authoritative for human-edited title, checkbox and notes.
                if (end <= i + 1) throw new InvalidDataException("Missing task line.");
                var task = TaskLine.Match(lines[i + 1].Value.TrimEnd('\r', '\n'));
                if (!task.Success) throw new InvalidDataException("Invalid managed task line.");
                item.Title = task.Groups[2].Value;
                bool renderedCompleted = task.Groups[1].Value != " ";
                if (renderedCompleted != item.IsCompleted || !renderedCompleted)
                {
                    invalidatedCompletion[item.Id] = item.CompletedAt;
                    item.CompletedAt = null;
                }
                else if (invalidatedCompletion.TryGetValue(item.Id, out var stale) && stale == item.CompletedAt)
                    item.CompletedAt = null;
                item.IsCompleted = renderedCompleted;
                var notes = new List<string>();
                for (int n = i + 2; n < end; n++)
                {
                    string note = lines[n].Value.TrimEnd('\r', '\n');
                    if (!note.StartsWith("  ", StringComparison.Ordinal)) throw new InvalidDataException("Invalid managed note line.");
                    notes.Add(note[2..]);
                }
                item.Notes = string.Join("\n", notes);
                int length = lines[end].Index + lines[end].Length - line.Index;
                yield return new Entry(path, line.Index, length, text.Substring(line.Index, length), item);
                i = end;
            }
            else
            {
                var match = TaskLine.Match(value);
                if (!match.Success) continue;
                int count = occurrences.GetValueOrDefault(value);
                occurrences[value] = count + 1;
                string key = Path.GetRelativePath(root, path).Replace('\\', '/') + "\n" + value + "\n" + count;
                string id = "import-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
                var month = Path.GetFileNameWithoutExtension(path);
                var day = headingDate ?? (DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date : new DateOnly(1970, 1, 1));
                var midnight = day.ToDateTime(TimeOnly.MinValue);
                var created = new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
                var notes = new List<string>();
                int last = i;
                while (last + 1 < lines.Length)
                {
                    string note = lines[last + 1].Value.TrimEnd('\r', '\n');
                    if (note.StartsWith("  ", StringComparison.Ordinal)) notes.Add(note[2..]);
                    else if (note.StartsWith('\t')) notes.Add(note[1..]);
                    else break;
                    last++;
                }
                int length = lines[last].Index + lines[last].Length - line.Index;
                yield return new Entry(path, line.Index, length, text.Substring(line.Index, length), new TaskItem { Id = id, Title = match.Groups[2].Value, Notes = string.Join("\n", notes), IsCompleted = match.Groups[1].Value != " ", CreatedAt = created });
                i = last;
            }
        }
    }

    private static string Serialize(TaskItem item, string nl)
    {
        if (item.Title.Contains('\n') || item.Title.Contains('\r')) throw new ArgumentException("Title must be a single line.");
        // The default JSON encoder escapes HTML-sensitive characters (including < and >).
        string meta = JsonSerializer.Serialize(new Metadata(item.Id, item.CreatedAt, item.IsCompleted ? item.CompletedAt : null, item.DeletedAt, item.IsCompleted));
        var result = Prefix + meta + " -->" + nl + "- [" + (item.IsCompleted ? "x" : " ") + "] " + item.Title + nl;
        if (item.Notes.Length > 0)
            result += string.Join(nl, item.Notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(s => "  " + s)) + nl;
        return result + End + nl;
    }
}

public static class DailyReport
{
    public static string Build(IEnumerable<TaskItem> items, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(items);
        var active = items.Where(t => t.DeletedAt is null && LocalDay(t.CreatedAt) <= day).OrderBy(t => t.CreatedAt).ToList();
        var output = new StringBuilder($"# 日报 {day:yyyy-MM-dd}\n\n");
        Section("今日完成", active.Where(t => t.IsCompleted && t.CompletedAt is { } completed && LocalDay(completed) == day), true);
        Section("当前待办", active.Where(t => !t.IsCompleted || t.CompletedAt is { } completed && LocalDay(completed) > day), false);
        Section("完成日期待确认", active.Where(t => t.IsCompleted && t.CompletedAt is null), true);
        output.Append("## 明日计划\n\n（待填写）\n");
        return output.ToString();

        void Section(string title, IEnumerable<TaskItem> tasks, bool completed)
        {
            output.Append("## ").Append(title).Append("\n\n");
            foreach (var task in tasks)
            {
                output.Append(completed ? "- [x] " : "- [ ] ").Append(task.Title).Append('\n');
                if (task.Notes.Length > 0)
                    foreach (string line in task.Notes.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                        output.Append("  ").Append(line).Append('\n');
            }
            output.Append('\n');
        }
    }

    private static DateOnly LocalDay(DateTimeOffset timestamp) => DateOnly.FromDateTime(timestamp.LocalDateTime);
}

