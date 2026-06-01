# NRSGitCheck — Requirements

## 1. Purpose

NRSGitCheck is a desktop application that makes it easy to review the changes in
a working copy of a Git repository by comparing the **current working tree
(uncommitted changes)** against a chosen reference point. It is a focused,
read-only diff *viewer* — its job is to answer "what have I changed, and how
does it differ from X?" quickly and clearly.

The comparison target (the "base" side of the diff) can be any of:

- **The last commit on the current branch** (`HEAD`).
- **Any other branch** in the repository (local or remote-tracking).
- **The original base of the current branch** — the merge-base between the
  current branch and its parent/upstream branch (i.e. where this branch diverged).

The "current" side of the diff is always the **working tree**, including
uncommitted (modified, added, deleted, untracked) files.

## 2. Scope

### In scope
- Selecting a repository and viewing its working-tree changes.
- Choosing a comparison target (last commit / another branch / branch base).
- A list of changed files with change-type indicators.
- A diff view showing original and new code with standard git-style decorations.
- Syntax highlighting layered under the diff decorations.
- Side-by-side and inline (unified) diff layouts, toggleable.
- Keyboard-driven navigation between files and between changes (hunks).
- Light/dark theme selection.
- Recent-repository history for quick re-opening.
- Modern, clean visual design.

### Out of scope (this version)
- **Any write operations to the repository.** No staging, unstaging,
  committing, discarding, editing, branching, pushing, or pulling. The app is
  strictly read-only against the repo.
- Merge-conflict resolution.
- Editing files in place.
- Remote/network operations (fetch, clone, auth).
- Multi-repository side-by-side views.

## 3. Platform & Technology

| Concern | Decision |
|---|---|
| Target OS | **Windows only** (initial release). |
| UI framework | **Avalonia 12** (Fluent theme, Inter font) — already established in the project. |
| Runtime | **.NET 10** (`net10.0`), `WinExe`. |
| Git access | **LibGit2Sharp** — native managed Git library; no dependency on a system `git` install. |
| Diff/editor control | **AvaloniaEdit** with **TextMate** grammars for syntax highlighting. |
| Settings storage | **Local JSON file** under the user's AppData folder. |
| Architecture | MVVM (Avalonia compiled bindings are already enabled). |

> Although LibGit2Sharp and Avalonia are cross-platform, only Windows is a
> supported/tested target for this release. The code should avoid Windows-only
> APIs where reasonable so a later cross-platform expansion stays cheap.

## 4. Functional Requirements

### 4.1 Repository selection & history
- FR-1. The user can open a repository by browsing to a folder.
- FR-2. The app validates that the chosen folder is (or is inside) a valid Git
  working directory; if not, it shows a clear, non-blocking error.
- FR-3. The app maintains a **history of recently opened repositories** and
  presents it for quick selection (e.g. a dropdown / recent list on a start
  screen and/or in the repo picker).
- FR-4. Each history entry shows at least the repo name/path; most-recent first.
- FR-5. The user can remove an entry from history. Entries pointing to folders
  that no longer exist are visually flagged and selectable for removal.
- FR-6. The most recently used repository may be re-opened automatically on
  launch (configurable; see settings).

### 4.2 Comparison target selection
- FR-7. The user can select the comparison base from three modes:
  1. **Last commit** (`HEAD` of the current branch).
  2. **Another branch** — chosen from a list of available branches.
  3. **Branch base** — the merge-base of the current branch against its
     upstream/parent branch.
- FR-8. For "another branch", the app lists **local branches only**, with the
  current branch indicated and the list searchable/filterable when long.
  Remote-tracking branches are not shown.
- FR-9. For "branch base", the app determines the parent branch. The default
  parent is the configured upstream; if none, the user is prompted to pick the
  branch to compute the merge-base against (e.g. `main`/`master`).
- FR-10. Changing the comparison target re-computes the file list and diffs and
  updates the UI without requiring the repo to be re-opened.
- FR-11. The current branch name and the resolved comparison target (branch
  name + short commit SHA) are always visible in the UI.

### 4.3 Changed-files list
- FR-12. The app shows a list of files that differ between the working tree and
  the comparison target.
- FR-13. Each entry shows the file path (with the filename emphasized) and a
  **change-type indicator**: Added, Modified, Deleted, Renamed, Untracked.
- FR-14. The list shows a count of changed files and may show per-file
  added/removed line counts.
- FR-15. Selecting a file loads its diff into the diff view.
- FR-16. The list supports filtering by filename and (optionally) by change type.
- FR-17. Binary files are listed but shown in the diff view as "binary file —
  no text diff" rather than attempting to render content.

### 4.4 Diff view
- FR-18. The diff view shows the **original (base) code** and the **new (working
  tree) code** with standard git-style decorations:
  - Added lines, removed lines, and modified/changed lines are color-coded.
  - Gutter line numbers for both the old and new sides.
  - Hunk separators between non-contiguous change regions.
- FR-19. The diff view supports two layouts, **toggleable** by the user:
  - **Side-by-side**: old on the left, new on the right.
  - **Inline / unified**: single column, classic `+`/`-` style.
- FR-20. **Syntax highlighting** (language-aware, via TextMate grammars) is
  applied to code content and composes cleanly with the diff decorations.
  Language is inferred from file extension; unknown types fall back to plain text.
- FR-21. Added files show all content as added; deleted files show all content
  as removed.
- FR-22. The diff view handles large files gracefully (virtualized rendering;
  a size threshold above which the app warns and/or offers to skip rendering).
- FR-23. **Intra-line (word-level) diff highlighting** is required for modified
  lines: within a changed line, the specific added/removed words or characters
  are highlighted more strongly than the surrounding unchanged text, in both
  side-by-side and inline layouts.

### 4.5 Navigation & keyboard shortcuts
- FR-24. The user can move between files and between individual changes (hunks)
  using the keyboard.
- FR-25. At minimum the following actions are bound to shortcuts:
  - Next file / previous file.
  - Next change (hunk) / previous change (hunk) within the current file.
  - Open repository.
  - Toggle diff layout (side-by-side / inline).
  - Toggle theme (light / dark).
  - Refresh / re-scan changes.
  - Focus the file-filter / search box.
- FR-26. **The shortcuts are surfaced in the UI** as a persistent reminder —
  e.g. inline hints next to controls and/or a help/legend panel or overlay
  (such as a "?" shortcuts cheat-sheet).
- FR-27. "Next change" navigation crosses file boundaries sensibly (advancing
  past the last hunk of a file moves to the first hunk of the next file).

#### Proposed default key bindings
> Final bindings to be confirmed during implementation; listed here so the UI
> hint text and help panel have a concrete starting point.

| Action | Shortcut |
|---|---|
| Next change / hunk | `J` or `Alt+Down` |
| Previous change / hunk | `K` or `Alt+Up` |
| Next file | `Ctrl+Down` or `]` |
| Previous file | `Ctrl+Up` or `[` |
| Toggle diff layout | `Ctrl+L` |
| Toggle theme | `Ctrl+T` |
| Open repository | `Ctrl+O` |
| Refresh changes | `F5` |
| Focus file filter | `Ctrl+F` |
| Show shortcuts help | `?` or `F1` |

### 4.6 Theming
- FR-28. The app provides a **light/dark mode selector** that switches the
  entire UI, including the diff decoration palette and syntax-highlighting theme.
- FR-29. By **default the app follows the system theme** (OS light/dark setting)
  and updates live when the system setting changes.
- FR-30. The user can **override** the system theme by explicitly selecting Light
  or Dark; the theme selector therefore offers three states: **System / Light /
  Dark**.
- FR-31. The selected theme mode (including an explicit override) persists across
  sessions. While in System mode, the app continues to track the OS setting.

### 4.7 Settings & persistence
- FR-32. Settings are stored in a JSON file in the user's AppData folder
  (e.g. `%APPDATA%\NRSGitCheck\settings.json`).
- FR-33. Persisted settings include at least: selected theme mode
  (System/Light/Dark), recent-repository list, last-used comparison mode, last
  diff layout, and whether to reopen the last repo on launch.
- FR-34. Corrupt or missing settings files are handled gracefully by falling
  back to defaults without crashing.

## 5. Non-Functional Requirements

- NFR-1. **Performance** — Opening a repository and computing the working-tree
  diff for a typical repo should feel responsive (sub-second to a couple of
  seconds). Long operations run off the UI thread with a progress/busy
  indicator; the UI never blocks.
- NFR-2. **Design** — Modern, clean, uncluttered visual design. Clear visual
  hierarchy (repo/target bar, file list, diff pane). Consistent spacing,
  readable monospace font for code, and accessible color contrast in both themes.
- NFR-3. **Reliability** — The app must never modify the repository. All Git
  access is read-only.
- NFR-4. **Robustness** — Gracefully handle: not-a-repo folders, empty repos,
  repos with no commits, detached HEAD, no upstream configured, deleted repo
  folders in history, binary and very large files, and files with unusual
  encodings/line endings.
- NFR-5. **Architecture/Maintainability** — MVVM with a clean separation
  between a Git service layer (LibGit2Sharp), view models, and views, so the
  Git backend or platform target can evolve without UI rewrites.

## 6. Proposed UI Layout

The app is a **single-screen, single-window** design — there are no separate
pages to navigate between. The same window shows either an empty state (no repo
open) or the review layout (repo open); the only transient surface is the
keyboard-shortcuts overlay.

- **Top bar** — repository selector (with recent-repo dropdown), current branch
  label, comparison-target selector (mode: last commit / branch / branch base,
  plus the branch picker), diff-layout toggle, theme toggle, refresh, and a
  "shortcuts" (`?`) button.
- **Empty state (no repo open)** — occupies the content area in place of the file
  list and diff pane; offers "open repository" and the recent-repository history.
  Replaced in-place by the review layout once a repo is opened. This is **not** a
  separate page.
- **Left panel** — the changed-files list with change-type indicators, a filter
  box, and a changed-file count.
- **Main/right panel** — the diff view (side-by-side or inline) for the selected
  file, with gutter line numbers, diff decorations, and syntax highlighting.
- **Footer / status bar** — resolved comparison target (branch + short SHA),
  summary stats (files changed, lines added/removed), and brief inline reminders
  of the key navigation shortcuts.
- **Shortcuts overlay** — a transient cheat-sheet shown over the current content
  (`?`/`F1`) and dismissed; not a page.

## 7. Future Enhancements

- Cross-platform (macOS/Linux) support in a later release.
- Per-hunk or whole-file copy-to-clipboard of diffs.
- Configurable / user-remappable keyboard shortcuts.
- Optional viewing of diffs between two arbitrary commits (beyond working tree).
- Optionally include remote-tracking branches in the branch picker.
