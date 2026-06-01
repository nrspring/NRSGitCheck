# NRSGitCheck — Implementation Plan

This plan turns [Requirements.md](Requirements.md) into a concrete, phased build.
It is written for the current scaffold: Avalonia 12, .NET 10 (`net10.0`, `WinExe`),
Fluent theme, compiled bindings enabled, Inter font.

---

## 1. Architecture Overview

**Pattern:** MVVM with a thin Git service layer. Views are XAML + code-behind
only for wiring; all state and logic live in view models; Git/IO lives in
services behind interfaces so they can be tested and later swapped.

```
NRSGitCheck/
├─ Program.cs                 // entry point (exists)
├─ App.axaml(.cs)             // app bootstrap, DI container, theme application
├─ Views/
│  ├─ MainWindow.axaml(.cs)   // single window: top bar + content + status bar
│  ├─ EmptyStateView.axaml    // in-shell panel: repo picker + recent list (no repo open)
│  ├─ DiffView.axaml(.cs)     // hosts AvaloniaEdit side-by-side / inline
│  └─ ShortcutsOverlay.axaml  // transient keyboard cheat-sheet (overlay, not a page)
├─ ViewModels/
│  ├─ ViewModelBase.cs        // INotifyPropertyChanged base / ObservableObject
│  ├─ MainWindowViewModel.cs  // owns repo, target, file list, selected file
│  ├─ FileChangeViewModel.cs  // one row in the changed-files list
│  ├─ DiffViewModel.cs        // computed diff model for the selected file
│  └─ ComparisonTargetViewModel.cs
├─ Models/
│  ├─ RepositoryInfo.cs       // path, name, current branch
│  ├─ FileChange.cs           // path, ChangeKind, +/- counts, isBinary
│  ├─ DiffDocument.cs         // lines/hunks for old & new sides
│  ├─ DiffLine.cs             // text, LineKind, old/new line numbers, word spans
│  └─ AppSettings.cs          // theme mode, recent repos, last layout, etc.
├─ Services/
│  ├─ IGitService.cs / GitService.cs        // LibGit2Sharp wrapper (read-only)
│  ├─ IDiffEngine.cs / DiffEngine.cs        // line + word-level diff computation
│  ├─ ISettingsService.cs / SettingsService.cs // JSON load/save in AppData
│  ├─ IThemeService.cs / ThemeService.cs    // System/Light/Dark + OS tracking
│  └─ ISyntaxHighlightingService.cs / ...   // TextMate grammar resolution
└─ Infrastructure/
   ├─ RelayCommand.cs / AsyncRelayCommand.cs
   └─ KeyboardShortcuts.cs   // central binding table (also feeds the UI hints)
```

**Dependencies to add** (NuGet, into `NRSGitCheck.csproj`):

| Package | Purpose |
|---|---|
| `LibGit2Sharp` | Read-only Git access (FR-1..11). |
| `AvaloniaEdit` | Code/diff text rendering with gutters & line transformers. |
| `AvaloniaEdit.TextMate` + `TextMateSharp.Grammars` | Syntax highlighting (FR-20). |
| `CommunityToolkit.Mvvm` | `ObservableObject`, `RelayCommand` (reduces boilerplate). |

> No system `git` dependency — LibGit2Sharp is self-contained.

**Threading rule (NFR-1):** every Git/diff/IO operation runs on a background
thread (`Task.Run`); only view-model property updates marshal back to the UI
thread. A busy indicator is bound to an `IsBusy` flag.

---

## 2. Data Flow

1. User picks a repo → `GitService.OpenRepository(path)` validates and returns
   `RepositoryInfo` (current branch, local branches, HEAD sha).
2. User picks a comparison target → `MainWindowViewModel` resolves it to a base
   tree/commit:
   - **Last commit** → `HEAD`'s tree.
   - **Another branch** → selected local branch tip tree.
   - **Branch base** → `repo.ObjectDatabase.FindMergeBase(currentTip, parentTip)`.
3. `GitService.GetChanges(baseTree, workingTree)` → `IReadOnlyList<FileChange>`
   (working tree incl. untracked) → bound to the file list.
4. User selects a file → `DiffEngine.Compute(oldBlob, newBlob)` → `DiffDocument`
   (hunks, line kinds, word-level spans) → `DiffView` renders it.

---

## 3. Phased Delivery

Each phase is independently runnable so progress is visible early.

### Phase 0 — Project foundation
- Add NuGet packages listed above.
- Replace the placeholder `MainWindow` content with the MVVM shell skeleton.
- Wire a minimal DI container (Microsoft.Extensions.DependencyInjection or a
  simple manual composition root in `App.axaml.cs`).
- Add `ViewModelBase`, `RelayCommand`/`AsyncRelayCommand` (or CommunityToolkit).
- **Exit check:** app launches to an empty shell with no placeholder text.

### Phase 1 — Settings & recent-repo history (FR-3..6, FR-31..34)
- `AppSettings` model + `SettingsService` reading/writing
  `%APPDATA%\NRSGitCheck\settings.json` with graceful fallback on corrupt/missing.
- Recent-repo list: add on open, dedupe, most-recent-first, remove, flag missing
  folders.
- **Exit check:** settings round-trip across restarts; recent list persists.

### Phase 2 — Git service & repo open (FR-1..2, FR-7..11)
- `GitService` (LibGit2Sharp): open/validate repo, list **local** branches,
  current branch, resolve the three comparison modes, compute merge-base.
- Determine parent branch for "branch base": use configured upstream; if none,
  prompt the user to choose (default `main`/`master` if present).
- `EmptyStateView` (in-shell panel, shown via `HasRepo` flag) + folder browser
  (Avalonia `StorageProvider`); invalid folder → inline non-blocking error.
- Top-bar shows current branch + resolved target (branch + short SHA).
- **Exit check:** open a real repo; switching target recomputes with no reopen.

### Phase 3 — Changed-files list (FR-12..17)
- `GitService.GetChanges` comparing base tree vs working tree, including
  untracked files; map to `FileChange` with `ChangeKind` (Added/Modified/
  Deleted/Renamed/Untracked) and +/- counts.
- `FileChangeViewModel` rows with filename emphasis + change-type indicator/badge.
- Filter box (by name; optional by change type) and a changed-file count.
- Binary detection → flagged, no content diff attempted.
- **Exit check:** list matches `git status` for a test repo; filtering works.

### Phase 4 — Diff engine (FR-18, FR-21..23)
- `DiffEngine`: line-level diff (Myers) producing hunks with context, line kinds
  (Added/Removed/Modified/Context), and old/new line numbers.
- **Word-level (intra-line) diff** for modified line pairs → character/word spans
  (FR-23, now a hard requirement).
- Added-file = all added; deleted-file = all removed; large-file threshold with
  warn/skip; binary short-circuit.
- Pure and unit-testable (no UI dependency).
- **Exit check:** unit tests cover add/remove/modify, rename, large, binary,
  CRLF/encoding edge cases.

### Phase 5 — Diff view rendering (FR-18..22)
> Decision (Phase 5): rendered with a **custom virtualized control**, not
> AvaloniaEdit. A read-only diff with dual gutters and word runs is cleaner this
> way, and side-by-side becomes a single virtualized list of two-sided rows (no
> two-editor scroll sync). Phase 6 syntax highlighting uses TextMateSharp directly.
- `DiffView` backed by virtualized `ListBox`es of row view models:
  - Background colorization per line kind; gutter line numbers for old & new.
  - **Side-by-side** (one list, each row holds both sides) and **inline/unified**
    (one list) layouts, toggleable (FR-19), persisted to settings.
  - Word-level highlight runs on modified lines in both layouts.
  - Hunk separators; `ListBox` virtualization for large files (NFR-1).
- **Exit check:** both layouts render correctly with decorations + word spans.

### Phase 6 — Syntax highlighting (FR-20)
- `SyntaxHighlightingService` resolves TextMate grammar by file extension;
  unknown → plain text. Compose grammar coloring under diff backgrounds so both
  are visible. Grammar theme follows the active light/dark theme.
- **Exit check:** common languages highlight; diff decorations remain legible.

### Phase 7 — Theming (FR-28..31)
- `ThemeService` with three modes **System / Light / Dark**; default System,
  tracking the OS setting live via `Application.PlatformSettings`
  (`ColorValuesChanged`). Explicit Light/Dark overrides; persisted.
- Define light & dark palettes for UI, diff decorations, and TextMate theme so
  all three switch together.
- **Exit check:** OS theme change updates app live in System mode; override holds
  and persists across restart.

### Phase 8 — Navigation & shortcuts (FR-24..27)
- `KeyboardShortcuts` central table drives both `KeyBindings` and the UI hint
  text (single source of truth).
- Implement next/prev file, next/prev hunk (crossing file boundaries), toggle
  layout, toggle theme, open repo, refresh, focus filter, show help.
- Inline hint text in the status bar + `ShortcutsOverlay` cheat-sheet (`?`/`F1`).
- Default bindings per Requirements §4.5.
- **Exit check:** full keyboard-only review flow works; hints match real bindings.

### Phase 9 — Design polish & hardening (NFR-2..4)
- Visual pass: spacing, hierarchy, monospace code font, contrast in both themes,
  busy indicator, empty/error states.
- Robustness pass: not-a-repo, empty repo, no commits, detached HEAD, no
  upstream, deleted history folders, binary/large/odd-encoding files.
- **Exit check:** edge-case matrix handled without crashes; UI feels clean.

---

## 4. Key Technical Decisions & Risks

- **Working-tree vs untracked diffs:** LibGit2Sharp `Compare<TreeChanges>` and
  `Diff.Compare` against `DiffTargets.WorkingDirectory`; include untracked via
  status options. Verify rename detection is enabled.
- **Side-by-side sync:** two AvaloniaEdit instances with linked scroll offset and
  aligned blank "filler" rows so old/new lines stay row-aligned across hunks.
- **Word-level diff cost:** only run on modified line pairs within a hunk, not
  whole files, to bound cost.
- **TextMate + diff layering:** confirm AvaloniaEdit allows both a TextMate
  installation and custom background transformers on the same editor; if they
  conflict, render diff backgrounds in a lower layer.
- **Large files:** enforce a size/line threshold (e.g. configurable) with a
  warn/skip path to keep the UI responsive.

---

## 5. Testing Strategy

- **Unit tests** (new `NRSGitCheck.Tests` project): `DiffEngine` (line + word),
  `SettingsService` (round-trip, corrupt file), comparison-target resolution
  (incl. merge-base), `FileChange` mapping. Git tests run against temporary repos
  created in `TestInitialize`.
- **Manual/UI smoke** per phase exit check, plus the Phase 9 edge-case matrix.

---

## 6. Suggested Build Order Summary

| Phase | Deliverable | Primary requirements |
|---|---|---|
| 0 | MVVM shell + deps | scaffold |
| 1 | Settings + recent repos | FR-3..6, 31..34 |
| 2 | Git service + repo open + targets | FR-1..2, 7..11 |
| 3 | Changed-files list | FR-12..17 |
| 4 | Diff engine (line + word) | FR-18, 21..23 |
| 5 | Diff view (both layouts) | FR-18..22 |
| 6 | Syntax highlighting | FR-20 |
| 7 | Theming (System/Light/Dark) | FR-28..31 |
| 8 | Navigation + shortcuts | FR-24..27 |
| 9 | Polish + hardening | NFR-2..4 |

---

## 7. Open Implementation Notes

- Pick DI approach (MS.DI vs manual) at Phase 0 and keep it consistent.
- Single-screen design: the empty state is an in-shell panel (`EmptyStateView`)
  swapped with the review layout by a `HasRepo` flag in the one `MainWindow` —
  no separate Start window/page.
- Confirm exact AvaloniaEdit 12 API names against the installed version before
  Phase 5 (transformer/colorizer signatures changed across versions).
