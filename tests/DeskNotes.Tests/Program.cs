using DeskNotes.Core;
using System.Text;

var tests = new (string Name, Action<string> Run)[]
{
    ("Unicode and multiline roundtrip, independent completion, deletion, backups", root =>
    {
        var store = new MarkdownStore(root);
        var task = new TaskItem { Title = "中文 🎉 café", Notes = "首行\r\n\r\n下一行 <!-- /desknote -->\r\n", CreatedAt = DateTimeOffset.Parse("2026-09-30T23:30:00+08:00"), IsCompleted = true };
        store.Save(task);
        var loaded = new MarkdownStore(root).Load().Single();
        Check(loaded.Id == task.Id && loaded.Title == task.Title && loaded.Notes == task.Notes.Replace("\r\n", "\n"), "roundtrip");
        Check(loaded.CreatedAt == task.CreatedAt && loaded.IsCompleted && loaded.CompletedAt is null, "unknown completion");
        Check(File.Exists(Path.Combine(root, "records", "2026-09.md")), "creation month");
        task.CompletedAt = DateTimeOffset.Parse("2026-10-01T01:00:00+08:00");
        task.DeletedAt = DateTimeOffset.Parse("2026-10-02T08:00:00+08:00");
        store.Save(task);
        loaded = new MarkdownStore(root).Load().Single();
        Check(loaded.DeletedAt == task.DeletedAt && loaded.CompletedAt == task.CompletedAt, "deletion and completion metadata");
        Check(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak").Length == 1, "backup");
        Check(!DailyReport.Build([loaded], new DateOnly(2026, 9, 30)).Contains(task.Title), "deleted excluded");
    }),
    ("External edits and plain imports preserved without load writes", root =>
    {
        var store = new MarkdownStore(root);
        var task = new TaskItem { Title = "managed", CreatedAt = DateTimeOffset.Parse("2026-10-03T12:00:00Z") };
        store.Save(task);
        string path = Path.Combine(root, "records", "2026-10.md");
        string external = "\n## 用户笔记\nKeep **this** exactly.\n- [x] external task\n";
        File.AppendAllText(path, external);
        byte[] before = File.ReadAllBytes(path);
        var importStore = new MarkdownStore(root);
        var imported = importStore.Load().Single(t => t.Title == "external task");
        Check(File.ReadAllBytes(path).SequenceEqual(before), "Load is read-only");
        Check(imported.IsCompleted && imported.CompletedAt is null, "plain unknown completion");
        File.WriteAllText(path, "intro\n" + File.ReadAllText(path), new UTF8Encoding(true));
        Check(new MarkdownStore(root).Load().Any(t => t.Id == imported.Id), "stable import after unrelated insertion");
        task.Notes = "local notes";
        store.Save(task);
        Check(File.ReadAllText(path).EndsWith(external), "unrelated content exact");
        Check(File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] {239,187,191}), "BOM preserved");
        imported.Notes = "imported notes";
        importStore.Save(imported);
        Check(new MarkdownStore(root).Load().Single(t => t.Id == imported.Id).Notes == "imported notes", "import promoted with same id");
    }),
    ("Same task conflict and unrelated task merge", root =>
    {
        var writer = new MarkdownStore(root);
        writer.Save(new TaskItem { Id = "a", Title = "A" });
        writer.Save(new TaskItem { Id = "b", Title = "B" });
        var first = new MarkdownStore(root);
        var second = new MarkdownStore(root);
        var a = first.Load().Single(t => t.Id == "a");
        var b = second.Load().Single(t => t.Id == "b");
        a.Notes = "first"; first.Save(a);
        b.Notes = "second"; second.Save(b);
        Check(new MarkdownStore(root).Load().All(t => t.Notes.Length > 0), "unrelated merge");
        var staleStore = new MarkdownStore(root);
        var stale = staleStore.Load().Single(t => t.Id == "a");
        a.Title = "new"; first.Save(a);
        stale.Title = "stale";
        Conflict(() => staleStore.Save(stale));
        Conflict(() => new MarkdownStore(root).Save(a));
        string path = Directory.GetFiles(Path.Combine(root, "records"), "*.md").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("- [ ] new", "- [x] edited externally"));
        Conflict(() => first.Save(a));
        var human = new MarkdownStore(root).Load().Single(t => t.Id == "a");
        Check(human.IsCompleted && human.Title == "edited externally" && human.CompletedAt is null, "human Markdown edits loaded");
    }),
    ("Missing task and malformed metadata cannot be overwritten", root =>
    {
        var store = new MarkdownStore(root);
        var task = new TaskItem { Title = "test" };
        store.Save(task);
        string path = Directory.GetFiles(Path.Combine(root, "records"), "*.md").Single();
        File.WriteAllText(path, "external replacement\n");
        Conflict(() => store.Save(task));
        Check(File.ReadAllText(path) == "external replacement\n", "missing task protection");
        File.WriteAllText(path, "<!-- desknote:broken -->\n");
        try { store.Save(new TaskItem()); throw new Exception("Expected malformed error"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(path) == "<!-- desknote:broken -->\n", "malformed preserved");
    }),
    ("Duplicate ids and creation month changes rejected; backup contains original", root =>
    {
        var store = new MarkdownStore(root);
        var task = new TaskItem { Title = "original", CreatedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z") };
        store.Save(task);
        string path = Path.Combine(root, "records", "2026-09.md");
        byte[] original = File.ReadAllBytes(path);
        task.Title = "updated";
        store.Save(task);
        Check(File.ReadAllBytes(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak").Single()).SequenceEqual(original), "backup matches original bytes");
        task.CreatedAt = task.CreatedAt.AddMonths(1);
        Conflict(() => store.Save(task));
        File.Copy(path, Path.Combine(root, "records", "2026-10.md"));
        Conflict(() => new MarkdownStore(root).Load());
        Conflict(() => store.Save(new TaskItem()));
    }),
    ("Daily report Chinese sections, local calendar and historical backlog", root =>
    {
        var day = new DateOnly(2026, 10, 3);
        var created = Local(new DateOnly(2026, 10, 1), 9);
        var today = new TaskItem { Title = "今日完成任务", Notes = "完成备注\n第二行", CreatedAt = created, IsCompleted = true, CompletedAt = Local(day, 0).ToOffset(TimeSpan.FromHours(-10)) };
        var later = new TaskItem { Title = "稍后完成", CreatedAt = created, IsCompleted = true, CompletedAt = Local(day.AddDays(1), 12) };
        var past = new TaskItem { Title = "过去已完成", CreatedAt = created, IsCompleted = true, CompletedAt = Local(day.AddDays(-1), 12) };
        var unknown = new TaskItem { Title = "日期未知", CreatedAt = created, IsCompleted = true };
        var pending = new TaskItem { Title = "普通待办", CreatedAt = created };
        var future = new TaskItem { Title = "未来创建", CreatedAt = Local(day.AddDays(1), 0) };
        string report = DailyReport.Build([today, later, past, unknown, pending, future], day);
        Check(Section(report, "今日完成").Contains("今日完成任务") && Section(report, "今日完成").Contains("  完成备注\n  第二行"), "completion uses local date and includes notes");
        Check(Section(report, "当前待办").Contains("稍后完成") && Section(report, "当前待办").Contains("普通待办"), "as-of backlog includes later completion");
        Check(!Section(report, "当前待办").Contains("今日完成任务") && !Section(report, "当前待办").Contains("日期未知"), "sections separate completion state");
        Check(Section(report, "完成日期待确认").Contains("日期未知"), "unknown completion separate");
        Check(!report.Contains("未来创建") && !report.Contains("过去已完成"), "future created and old completed excluded");
        Check(Section(report, "明日计划").Trim() == "（待填写）", "plan never filled with backlog");
    }),
    ("Readable metadata escapes HTML and never duplicates title or notes", root =>
    {
        var store = new MarkdownStore(root);
        var item = new TaskItem { Id = "unsafe-->quote\"<&", Title = "unique-title", Notes = "unique-note", CreatedAt = Local(new DateOnly(2026, 10, 3), 10), CompletedAt = DateTimeOffset.Now };
        store.Save(item);
        string text = File.ReadAllText(Path.Combine(root, "records", "2026-10.md"));
        string metadata = text.Split('\n').Single(l => l.StartsWith("<!-- desknote:"));
        Check(metadata.Contains("{\"id\":") && metadata.Contains("\"isCompleted\":false"), "readable compact JSON");
        Check(!metadata.Contains(item.Title) && !metadata.Contains(item.Notes), "no duplicated content");
        Check(metadata.Contains("\\u003E") && metadata.Contains("\\u003C"), "unsafe HTML escaped");
        var loaded = store.Load().Single();
        Check(loaded.Id == item.Id && loaded.CompletedAt is null, "id roundtrip and pending clears timestamp");
    }),
    ("External checkbox transition invalidates stale completion", root =>
    {
        var store = new MarkdownStore(root);
        var item = new TaskItem { Title = "checkbox", IsCompleted = true, CompletedAt = DateTimeOffset.Now };
        store.Save(item);
        string path = Directory.GetFiles(Path.Combine(root, "records"), "*.md").Single();
        File.WriteAllText(path, File.ReadAllText(path).Replace("- [x] checkbox", "- [ ] checkbox"));
        var uncheckedItem = store.Load().Single();
        Check(!uncheckedItem.IsCompleted && uncheckedItem.CompletedAt is null, "external uncheck clears old time");
        File.WriteAllText(path, File.ReadAllText(path).Replace("- [ ] checkbox", "- [x] checkbox"));
        var rechecked = store.Load().Single();
        Check(rechecked.IsCompleted && rechecked.CompletedAt is null, "observed recheck cannot resurrect old time");
        store.Save(rechecked);
        Check(new MarkdownStore(root).Load().Single().CompletedAt is null, "unknown completion persists");
        rechecked.IsCompleted = false;
        rechecked.CompletedAt = DateTimeOffset.Now;
        store.Save(rechecked);
        File.WriteAllText(path, File.ReadAllText(path).Replace("- [ ] checkbox", "- [x] checkbox"));
        Check(new MarkdownStore(root).Load().Single().CompletedAt is null, "checkbox differs recorded bool");
    }),
    ("Month/day headings, local plain date and indented import notes", root =>
    {
        Directory.CreateDirectory(Path.Combine(root, "records"));
        string path = Path.Combine(root, "records", "2026-10.md");
        string original = "# 2026-10\r\n\r\n## 2026-10-02\r\n- [ ] plain\r\n  首行\r\n  \r\n\t第二行\r\n## 2026-10-03\r\nuntouched\r\n";
        File.WriteAllText(path, original);
        var store = new MarkdownStore(root);
        var plain = store.Load().Single();
        Check(plain.CreatedAt == Local(new DateOnly(2026, 10, 2), 0) && plain.CreatedAt.Offset == Local(new DateOnly(2026, 10, 2), 0).Offset, "heading local date and offset");
        Check(plain.Notes == "首行\n\n第二行", "plain indented notes");
        Check(File.ReadAllText(path) == original, "import load never rewrites");
        store.Save(plain);
        Check(store.Load().Single().Notes == plain.Notes, "materialized notes preserved");
        store.Save(new TaskItem { Title = "same day", CreatedAt = Local(new DateOnly(2026, 10, 2), 11) });
        string text = File.ReadAllText(path);
        Check(text.Split("## 2026-10-02").Length == 2, "no duplicated day heading");
        Check(text.IndexOf("same day") < text.IndexOf("## 2026-10-03"), "append into existing day");
        Check(text.EndsWith("## 2026-10-03\r\nuntouched\r\n"), "next section preserved");
        store.Save(new TaskItem { Title = "next month", CreatedAt = Local(new DateOnly(2026, 11, 1), 0).ToOffset(TimeSpan.FromHours(-10)) });
        string next = File.ReadAllText(Path.Combine(root, "records", "2026-11.md"));
        Check(next.StartsWith("# 2026-11\n") && next.Contains("## 2026-11-01"), "new local month and day headings");
    }),
    ("Backup retention bounded per monthly file", root =>
    {
        var store = new MarkdownStore(root);
        var task = new TaskItem { Title = "version-0", CreatedAt = Local(new DateOnly(2026, 9, 1), 12) };
        store.Save(task);
        for (int i = 1; i <= 55; i++) { task.Title = "version-" + i; store.Save(task); }
        var backups = Directory.GetFiles(Path.Combine(root, "backups"), "2026-09.md.*.bak").Order(StringComparer.Ordinal).ToArray();
        Check(backups.Length == 50, "retains latest 50");
        Check(File.ReadAllText(backups[0]).Contains("- [ ] version-5\n") && File.ReadAllText(backups[^1]).Contains("- [ ] version-54\n"), "retains correct versions");
        Check(Directory.GetFiles(Path.Combine(root, "records"), "*.bak").Length == 0, "no backups in records");
        var other = new TaskItem { Title = "other", CreatedAt = Local(new DateOnly(2026, 10, 1), 12) };
        store.Save(other); other.Notes = "changed"; store.Save(other);
        Check(Directory.GetFiles(Path.Combine(root, "backups"), "*.bak").Length == 51, "retention isolated by month");
    }),
};
int failures = 0;
foreach (var test in tests)
{
    string root = Path.Combine(Path.GetTempPath(), "DeskNotes.Tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { test.Run(root); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
    finally { Directory.Delete(root, true); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed");
return failures == 0 ? 0 : 1;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Conflict(Action action)
{
    try { action(); } catch (MarkdownConflictException) { return; }
    throw new Exception("Expected MarkdownConflictException");
}

static DateTimeOffset Local(DateOnly day, int hour)
{
    var time = day.ToDateTime(new TimeOnly(hour, 0));
    return new DateTimeOffset(time, TimeZoneInfo.Local.GetUtcOffset(time));
}
static string Section(string report, string title) => report.Split("## " + title + "\n")[1].Split("\n## ")[0];
