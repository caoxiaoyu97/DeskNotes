# DeskNotes.Core API

Namespace: `DeskNotes.Core`. Target: `net10.0`; no external packages.

- `TaskItem`: mutable `string Id` (defaults to Guid), `string Title`, `string Notes`, `DateTimeOffset CreatedAt`, `bool IsCompleted`, `DateTimeOffset? CompletedAt`, `DateTimeOffset? DeletedAt`.
- `MarkdownStore(string root)`: `List<TaskItem> Load()` includes metadata-deleted tasks; callers filter DeletedAt for active lists. `void Save(TaskItem item)` saves one task. Load before editing existing tasks. A new store may save new ids without Load.
- Only files named `yyyy-MM.md` inside `records/` are treated as data. Anything else (cloud-sync conflict copies such as `2026-10 (冲突副本).md`, scratch notes) is ignored, so a synced folder cannot make loading fail with duplicate ids.
- `MarkdownConflictException : IOException`: stale/missing task, duplicate ids, existing id without a baseline, or changed creation date. Malformed managed data raises InvalidDataException. Cooperative lock contention raises IOException; callers can retry. Instances require serialized access.
- `DailyReport.Build(IEnumerable<TaskItem>, DateOnly day)` returns Chinese Markdown with 今日完成, 当前待办, 完成日期待确认, 明日计划.

## Readable storage

Files are `root/records/yyyy-MM.md`, using the creation timestamp converted to system local time. New files start with `# yyyy-MM`; new tasks are placed under `## yyyy-MM-dd`. If the day already exists, append before the next level-one/two heading; otherwise append a new day section. Existing task saves replace only their source block.

```markdown
# 2026-10

## 2026-10-03
<!-- desknote:{"id":"abc","created":"2026-10-03T09:00:00+08:00","completed":null,"deleted":null,"isCompleted":false} -->
- [ ] 标题
  备注第一行
  备注第二行
<!-- /desknote -->
```

Metadata contains only id, created, completed, deleted, isCompleted. Default System.Text.Json HTML-safe escaping protects `<`, `>` and other unsafe string characters; title and notes are never duplicated in metadata. All five keys are required. Legacy base64 records are not supported (no existing production data). Notes use LF in memory; their blank lines and trailing newline are retained. Rendered title/checkbox/notes are authoritative. A title must be a single line.

`IsCompleted` is independent of whether the completion date is known. On Load, an unchecked task always has CompletedAt=null. When the rendered checkbox differs from recorded isCompleted, CompletedAt is cleared. A store instance remembers invalidated completion timestamps across Loads, so an observed external uncheck/recheck does not resurrect the old timestamp. Saving persists the normalized state, and saving an unchecked task always writes completed=null. Save does not mutate the supplied object. The caller supplies explicit completion/deletion timestamps; none are invented.

Read-only Load cannot detect an external uncheck/recheck that both occurred between reads and restored identical file contents, nor retain an unsaved observation across process restarts. Save the observed task to persist invalidation. Full reopen/completion history is not recorded.

## Plain Markdown imports

Plain `- [ ]` / `- [x]` tasks get deterministic ids from relative file path, exact checkbox/title line, and occurrence among identical lines; Load never rewrites files. Creation date uses the nearest preceding `## yyyy-MM-dd`, with local midnight and that date's local UTC offset. Without such a heading it uses the filename month's first day, falling back to local 1970-01-01 for nonmonthly filenames.

Contiguous following lines indented by two spaces or a tab are imported as Notes. One indentation prefix is removed; deeper indentation and indented blank lines remain. Unindented blank lines/end of indentation terminate the notes. Saving the import materializes its entire task-and-notes block with the same id and leaves surrounding text intact. Notes normalize line endings to LF; materialization follows the document newline convention.

Untagged identity changes if the task's exact line/path changes or identical duplicate occurrences shift; metadata is required for permanent identity. Notes edits keep the id and participate in conflict checks. Existing CreatedAt cannot be changed.

## Persistence and conflicts

Writes preserve surrounding UTF-8 Markdown and any BOM, flush a same-directory temporary file, compare the original file bytes again, and atomically replace the destination. Backups are under `root/backups/yyyy-MM.md.<UTC timestamp>.<unique id>.bak`, preserving exact prior bytes. Retention is the newest 50 per monthly filename; cleanup is best effort if another process holds a backup open. Cleanup failure does not falsely report a committed save as failed.

A cooperative store lock serializes writes. Exact task block comparison rejects stale edits while permitting unrelated edits to merge. Removed tasks, duplicate ids, and corrupt managed records stop writes. A non-cooperating editor can still race the final compare/replace window; this is not a filesystem transaction with arbitrary editors.

## Daily report semantics

All calendar comparisons use system local time (`DateTimeOffset.LocalDateTime`), consistent with desktop UI dates. Deleted tasks are excluded. Tasks created after the selected local date are excluded from every section.

- 今日完成: checked tasks with a known completion date equal to the selected date; includes indented Notes.
- 当前待办: unchecked tasks plus tasks whose known completion date is later than the selected date. Rendered as unchecked, reflecting the selected day's end.
- 完成日期待确认: checked tasks with null CompletedAt, without guessing a date.
- 明日计划: `（待填写）` only; backlog is never copied into the plan.

Notes are included in all populated task sections. Past completed tasks are omitted. Deleted tasks stay excluded even in historical reports; the single current model cannot reconstruct full deletion/reopening history.

## Validation

Run from repository root:

```powershell
.\.dotnet\dotnet.exe run --project tests/DeskNotes.Tests/DeskNotes.Tests.csproj -c Release
```

Ten console test groups currently pass; failure returns a nonzero exit code.
