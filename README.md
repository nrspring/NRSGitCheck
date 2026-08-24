# NRSGitCheck

A fast, **read-only** desktop viewer for your Git changes. Open a repository, pick what
to compare against, and browse the diff with syntax highlighting, word-level change
emphasis, side-by-side or inline layouts, and progressive rendering that stays smooth
even on very large files.

NRSGitCheck reads your repository and never modifies your work. The only things it
writes are ones you press a button for: **Pull main**, which fetches and
fast-forwards your main branch (fast-forward only, and it leaves your working tree
untouched unless main is checked out), and the branch switches and branch creation on
the **Repositories** tab, which are plain checkouts — Git itself refuses any switch
that would overwrite uncommitted work. Nothing is ever stashed, merged, or discarded
on your behalf.

![Side-by-side diff with syntax and word-level highlighting](docs/screenshots/side-by-side.png)

## Features

- **Compare against anything** — uncommitted changes, any commit back to the start of
  your branch, everything on the branch versus `main`, another local branch, or the
  merge-base with a parent branch.
- **Commit picker** — a dropdown of the commits on your branch (newest first, back to
  the branch point) so you can widen the diff one commit at a time.
- **Pull main** — fetch and fast-forward `main` without leaving your feature branch.
- **Repositories tab** — keep a list of the repositories you work in and see, at a
  glance, which branch each one is on and whether it has uncommitted changes or
  unpushed commits. Switch any of them to another local branch or to `main`, pull
  `main` in one, or pull `main` in every repository that currently has `main` (or
  `master`) checked out. The list is stored with your settings, so it survives restarts.
- **New branches from a pattern** — define a shape once, such as
  `nrs/{TodaysDate}-sa-{SANumber}-{description}`, and every `{placeholder}` becomes a
  field in the New branch dialog. Each field can be pre-filled by a small C# expression
  you write — `DateTime.Now.ToString("yyyyMMdd")`, say — which is compiled and run as
  you type it, so a typo is reported where you wrote it rather than when you need a
  branch (see [Branch name patterns](#branch-name-patterns)).
- **Review a pull request** — paste a PR link, and it is fetched onto a local
  `pr-N` branch, checked out, and compared against `main` — the same diff GitHub
  shows on the PR's Files-changed tab.
- **Side-by-side and inline diffs** — toggle layouts instantly. Side-by-side panes are
  equal-width, each with their own horizontal scrollbar, and scroll in sync.
- **Syntax highlighting** for the diffed file (TextMate grammars), layered with
  **word-level** add/remove emphasis on modified lines.
- **Whole-file or hunks** — view just the changed regions with context, or the entire
  file with the diff highlighting intact.
- **Select and copy lines** — click a line in either pane, shift-click to extend,
  ctrl-click to pick out individual ones, then `Ctrl+C` or right-click → Copy. What
  lands on the clipboard is plain code: no gutter numbers, no `+`/`−` markers, and no
  hunk headers. In side-by-side, each pane copies its own side.
- **Changed-files tree** with a live filter and per-file `+`/`−` line counts.
- **Progressive rendering** — large diffs stream in hunk-by-hunk so the top of a file is
  visible while the rest is still being computed (see [How it works](#how-it-works)).
- **Auto-refresh** — optionally poll the repository on an interval and update the change
  list when something new appears, without disturbing your current view.
- **Keyboard-driven** navigation: `N`/`P` step through every changed section in the
  file and then carry on into the next/previous file, without wrapping around at
  either end. Plus a help overlay.
- **Light / dark / system** theming, and it reopens your last repository on launch.

## Screenshots

**Dark theme**

| Side-by-side | Inline (unified) |
| --- | --- |
| ![Side-by-side, dark](docs/screenshots/side-by-side.png) | ![Inline, dark](docs/screenshots/inline.png) |

**Light theme**

| Side-by-side | Inline (unified) |
| --- | --- |
| ![Side-by-side, light](docs/screenshots/side-by-side-light.png) | ![Inline, light](docs/screenshots/inline-light.png) |

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows (the app is built as a Windows desktop app; the underlying
  [Avalonia](https://avaloniaui.net/) UI toolkit is cross-platform)

### Build & run

```bash
# from the repository root
dotnet run --project NRSGitCheck.csproj
```

To produce a build without running:

```bash
dotnet build -c Release
```

### Run the tests

```bash
dotnet test
```

## Usage

1. **Open a repository** with the *Repo* button (or `Ctrl+O`). Recently opened repos are
   remembered and shown as quick-pick pills.
2. Choose a **comparison target** from the *Compare against* dropdown:
   - **Uncommitted changes** — your working-tree changes since `HEAD`.
   - **Since commit…** — pick any commit on the branch; shows everything that changed
     after it, including work you haven't committed. Defaults to the branch point.
   - **All changes vs main** — everything on this branch, committed or not, measured
     from where it diverged from `main` (or `master`, or `origin/main`).
   - **Another branch** — diff against the tip of a chosen local branch.
   - **Branch base (merge-base)** — diff against where your branch diverged from a parent.
3. Pick a file in the tree to see its diff. Use the header buttons to switch between
   **inline / side-by-side** and to toggle **whole file** vs. changed regions.
4. Tick **Auto** to have the change list refresh itself periodically.
5. Press **Pull *main*** to fetch and fast-forward your main branch. When main isn't
   checked out this only moves the branch ref, so your working tree is untouched; if
   main has diverged the pull is refused rather than merged or rebased.
6. Press **Review PR…** and paste a pull request link (or just its number) to fetch
   it onto a local `pr-N` branch and switch to it. Your working tree must be clean,
   since checking out the PR replaces it. A link belonging to a different repository
   is rejected rather than fetched into this one.

### Keyboard shortcuts

| Action | Keys |
| --- | --- |
| Next changed section | `N` · `J` · `Alt+↓` |
| Previous changed section | `P` · `K` · `Alt+↑` |
| Next file | `Ctrl+↓` · `]` |
| Previous file | `Ctrl+↑` · `[` |
| Toggle diff layout | `Ctrl+L` |
| Toggle theme | `Ctrl+T` |
| Open repository | `Ctrl+O` |
| Refresh changes | `F5` |
| Focus file filter | `Ctrl+F` |
| Show shortcuts | `?` · `F1` |

## How it works

The diff engine is a pure, UI-free Myers shortest-edit-script implementation. For speed
on large files it first trims the common prefix/suffix and splits the file on
**patience-style anchors** (lines that occur exactly once on each side), then runs Myers
only on the small regions between anchors. Hunks are produced **lazily** and streamed to
the UI, so rendering of the first hunks overlaps with computation of the rest — the diff
appears progressively rather than freezing until it's done. Word-level highlighting and
syntax colors are applied per hunk as it arrives.

## Branch name patterns

The **Branch pattern…** button on the Repositories tab defines how new branches are
named. The pattern is ordinary text with `{placeholders}`:

```
nrs/{TodaysDate}-sa-{SANumber}-{description}
```

Every placeholder becomes a field in the **New branch** dialog. Each one can have a
default: a C# expression that returns a value, evaluated when the dialog opens.

| Field | Expression | Produces |
| --- | --- | --- |
| `TodaysDate` | `DateTime.Now.ToString("yyyyMMdd")` | `20260824` |
| `SANumber` | *(blank — you fill it in)* | |
| `description` | *(blank — you fill it in)* | |

Expressions are compiled as you type them, and the settings dialog shows either what
the expression currently produces or the compiler error, with its column. `System`,
`System.Linq`, `System.Text`, and `System.Globalization` are in scope, so short names
like `DateTime` work. An expression that fails is not fatal: its field simply starts
empty and the New branch dialog says why.

Field values are folded into a legal ref name — spaces become dashes, and characters
Git will not accept are dropped — and the assembled name is shown before you commit to
it. Creating the branch runs `git checkout -b`, so Git has the final say on the name.

These expressions are your own code, running in this process with no sandbox. They are
a formula field, not a plugin system: an expression only runs when you open the branch
dialogs, and a long-running one is abandoned after three seconds.

## Tech stack

- [.NET 10](https://dotnet.microsoft.com/) / C#
- [Avalonia](https://avaloniaui.net/) — cross-platform UI
- [LibGit2Sharp](https://github.com/libgit2/libgit2sharp) — read-only Git access
- The `git` CLI — used for the write actions (**Pull main**, PR checkout, branch
  switches and creation), so your existing credential helper handles authentication
  and the app never touches your secrets
- [Roslyn scripting](https://github.com/dotnet/roslyn) — compiles and runs the C#
  expressions behind branch-name fields
- [TextMateSharp](https://github.com/danipen/TextMateSharp) — syntax highlighting
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) — MVVM
- xUnit for tests

## Project layout

```
Models/        Domain types (diff documents, file changes, settings)
Services/      Git access, diff engine, syntax highlighting, settings, theming
ViewModels/    MVVM view models for the window and the diff view
Views/         Avalonia XAML views and code-behind
Converters/    Value converters for diff rendering
NRSGitCheck.Tests/  xUnit test suite
```

## License

Released under the [MIT License](LICENSE) — you're free to use, modify, and distribute
it for pretty much anything, including commercial use. See the [LICENSE](LICENSE) file
for details.
