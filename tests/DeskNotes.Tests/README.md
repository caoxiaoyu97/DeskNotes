# Console tests

Run from repository root:

```powershell
.\.dotnet\dotnet.exe run --project tests/DeskNotes.Tests/DeskNotes.Tests.csproj -c Release
```

No test packages are required. Each test uses a unique temporary store and removes it afterward; failures return nonzero.

Ten groups cover Unicode/multiline notes, unknown completion and deletion, external edits and BOM preservation, optimistic conflicts and unrelated merges, missing/corrupt data, duplicate ids, immutable creation dates, exact prior-byte backups, Chinese as-of reports using local dates, HTML-safe compact metadata, external checkbox transitions, local day headings, plain indented notes and materialization, and latest-50 backup retention per month.

Notes deliberately normalize CRLF/CR to LF in memory. Storage tests verify semantic text preservation and exact surrounding content; backup tests verify original bytes. Date fixtures use explicit local dates and differently offset timestamps to catch accidental comparisons against the source offset calendar.
