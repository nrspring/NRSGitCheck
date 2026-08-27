using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NRSGitCheck.Models;
using NRSGitCheck.Services;
using NRSGitCheck.ViewModels;
using Xunit;

namespace NRSGitCheck.Tests;

/// <summary>
/// Finding Visual Studio Code and handing a repository folder to it. The lookup is
/// exercised against a fake filesystem and environment, so these say what would be
/// launched without launching anything.
/// </summary>
public sealed class EditorTests
{
    private const string LocalAppData = @"C:\Users\test\AppData\Local";
    private const string UserInstall = @"C:\Users\test\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    private const string MachineInstall = @"C:\Program Files\Microsoft VS Code\Code.exe";

    private static Func<string, string?> Environment(string? path = null) => name => name switch
    {
        "LOCALAPPDATA" => LocalAppData,
        "ProgramFiles" => @"C:\Program Files",
        "ProgramFiles(x86)" => @"C:\Program Files (x86)",
        "PATH" => path,
        _ => null,
    };

    private static Func<string, bool> Present(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    // --- Locating the editor ------------------------------------------------

    [Fact]
    public void A_user_scope_install_is_found()
    {
        var found = VsCodeService.Locate(Present(UserInstall), Environment());

        Assert.Equal(UserInstall, found);
    }

    [Fact]
    public void A_machine_scope_install_is_found()
    {
        var found = VsCodeService.Locate(Present(MachineInstall), Environment());

        Assert.Equal(MachineInstall, found);
    }

    /// <summary>
    /// Both exist side by side; the executable is preferred over the PATH shim so no
    /// console window flashes on launch.
    /// </summary>
    [Fact]
    public void The_executable_is_preferred_over_the_launcher_on_path()
    {
        const string shim = @"C:\Users\test\AppData\Local\Programs\Microsoft VS Code\bin\code.cmd";

        var found = VsCodeService.Locate(
            Present(UserInstall, shim),
            Environment(path: @"C:\Users\test\AppData\Local\Programs\Microsoft VS Code\bin"));

        Assert.Equal(UserInstall, found);
    }

    [Fact]
    public void An_install_only_on_path_is_still_found()
    {
        const string scoop = @"C:\tools\vscode\code.cmd";

        var found = VsCodeService.Locate(Present(scoop), Environment(path: @"C:\tools\vscode"));

        Assert.Equal(scoop, found);
    }

    [Fact]
    public void Nothing_installed_means_nothing_found()
    {
        Assert.Null(VsCodeService.Locate(Present(), Environment(path: @"C:\windows")));
    }

    [Fact]
    public void An_empty_path_is_not_a_failure()
    {
        Assert.Null(VsCodeService.Locate(Present(), Environment(path: null)));
        Assert.Null(VsCodeService.Locate(Present(), Environment(path: string.Empty)));
    }

    [Fact]
    public void Blank_and_quoted_path_entries_are_survived()
    {
        const string exe = @"C:\tools\vscode\Code.exe";

        var found = VsCodeService.Locate(Present(exe), Environment(path: @";;""C:\tools\vscode"";"));

        Assert.Equal(exe, found);
    }

    [Fact]
    public void Availability_follows_whether_one_was_found()
    {
        Assert.True(new VsCodeService(Present(UserInstall), Environment()).IsAvailable);
        Assert.False(new VsCodeService(Present(), Environment()).IsAvailable);
    }

    // --- Opening ------------------------------------------------------------

    [Fact]
    public void Opening_without_an_editor_installed_fails_rather_than_throwing()
    {
        var service = new VsCodeService(Present(), Environment());

        var result = service.Open(Path.GetTempPath());

        Assert.False(result.Success);
        Assert.Contains("Visual Studio Code", result.Message);
    }

    [Fact]
    public void Opening_a_folder_that_is_gone_fails_before_launching()
    {
        var service = new VsCodeService(Present(UserInstall), Environment());

        var result = service.Open(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

        Assert.False(result.Success);
        Assert.Contains("no longer on disk", result.Message);
    }

    [Fact]
    public void Opening_nothing_fails()
    {
        var service = new VsCodeService(Present(UserInstall), Environment());

        Assert.False(service.Open(string.Empty).Success);
        Assert.False(service.Open("   ").Success);
    }

    // --- The row's command --------------------------------------------------

    [Fact]
    public void The_button_opens_this_repository_folder()
    {
        var fixture = new Fixture();

        Assert.True(fixture.Row.OpenInEditorCommand.CanExecute(null));
        fixture.Row.OpenInEditorCommand.Execute(null);

        Assert.Equal(@"C:\repo", Assert.Single(fixture.Editor.Opened));
        Assert.Contains("Opened in Visual Studio Code", fixture.Owner.Status);
    }

    [Fact]
    public void The_button_is_dead_when_the_editor_is_not_installed()
    {
        var fixture = new Fixture(editorAvailable: false);

        Assert.False(fixture.Row.OpenInEditorCommand.CanExecute(null));
        Assert.Contains("was not found", fixture.Row.OpenInEditorToolTip);
    }

    [Fact]
    public void The_button_is_dead_on_a_repository_that_could_not_be_read()
    {
        var fixture = new Fixture();
        fixture.Row.Apply(RepositoryStatus.Failed(@"C:\repo", "repo", "Folder is missing."));

        Assert.False(fixture.Row.OpenInEditorCommand.CanExecute(null));
    }

    [Fact]
    public void A_failed_launch_is_reported()
    {
        var fixture = new Fixture();
        fixture.Editor.Refusal = "Could not start Visual Studio Code.";

        fixture.Row.OpenInEditorCommand.Execute(null);

        Assert.Contains("Could not start", fixture.Owner.ErrorMessage);
    }

    [Fact]
    public void The_tooltip_names_the_editor_when_it_is_there()
    {
        var fixture = new Fixture();

        Assert.Contains("Visual Studio Code", fixture.Row.OpenInEditorToolTip);
        Assert.DoesNotContain("was not found", fixture.Row.OpenInEditorToolTip);
    }

    // --- Fixture ------------------------------------------------------------

    private sealed class Fixture
    {
        public RecordingEditorService Editor { get; } = new();
        public RepositoriesViewModel Owner { get; }
        public TrackedRepositoryViewModel Row { get; }

        public Fixture(bool editorAvailable = true)
        {
            Editor.IsAvailable = editorAvailable;

            var settings = new StubSettings();
            settings.Settings.TrackedRepositories.Add(new TrackedRepository { Path = @"C:\repo", Name = "repo" });

            Owner = new RepositoriesViewModel(
                settings, new StubStatusService(), new StubGitCommands(), new StubFolderPicker(),
                new StubEvaluator(), new StubClipboard(), Editor);

            Row = Owner.Repositories.Single();
            Row.Apply(Valid());
        }

        private static RepositoryStatus Valid() => new(
            Path: @"C:\repo", Name: "repo", IsValid: true, Error: null, CurrentBranch: "main",
            IsDetachedHead: false, IsHeadUnborn: false, LocalBranches: new[] { "main" },
            MainBranch: "main", UncommittedCount: 0, HasUpstream: true, AheadBy: 0, BehindBy: 0,
            HasRemote: true, Changes: Array.Empty<WorkingTreeChange>(), UntrackedCount: 0);
    }

    private sealed class StubStatusService : IRepositoryStatusService
    {
        public RepositoryStatus Read(string path) =>
            RepositoryStatus.Failed(path, "repo", "not read");

        public System.Threading.Tasks.Task<IReadOnlyList<RepositoryStatus>> ReadAllAsync(
            IEnumerable<string> paths, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult<IReadOnlyList<RepositoryStatus>>(paths.Select(Read).ToList());
    }

    private sealed class StubGitCommands : IGitCommandService
    {
        public System.Threading.Tasks.Task<GitCommandResult> PullMainAsync(
            string workingDirectory, string? mainBranch, string? currentBranch,
            System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "up to date"));

        public System.Threading.Tasks.Task<GitCommandResult> CheckoutPullRequestAsync(
            string workingDirectory, PullRequestReference pr, string? currentBranch,
            System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "checked out"));

        public System.Threading.Tasks.Task<GitCommandResult> CheckoutBranchAsync(
            string workingDirectory, string branch, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "checked out"));

        public System.Threading.Tasks.Task<GitCommandResult> CreateBranchAsync(
            string workingDirectory, string branch, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "created"));

        public System.Threading.Tasks.Task<GitCommandResult> PushAsync(
            string workingDirectory, string branch, bool setUpstream,
            System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "pushed"));

        public System.Threading.Tasks.Task<GitCommandResult> CommitAllAsync(
            string workingDirectory, string message, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "committed"));

        public System.Threading.Tasks.Task<GitCommandResult> DiscardChangesAsync(
            string workingDirectory, bool deleteUntrackedFiles, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new GitCommandResult(true, "discarded"));
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public void Load() { }
        public void Save() { }
        public void AddRecentRepository(string repositoryPath) { }
        public void RemoveRecentRepository(string repositoryPath) { }
        public bool AddTrackedRepository(string repositoryPath) => false;
        public void RemoveTrackedRepository(string repositoryPath) { }
    }

    private sealed class StubFolderPicker : IFolderPickerService
    {
        public System.Threading.Tasks.Task<string?> PickFolderAsync(string title) =>
            System.Threading.Tasks.Task.FromResult<string?>(null);
    }

    private sealed class StubEvaluator : IExpressionEvaluator
    {
        public string? Validate(string? code) => null;

        public System.Threading.Tasks.Task<ExpressionResult> EvaluateAsync(
            string? code, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new ExpressionResult(true, string.Empty, null));
    }

    private sealed class StubClipboard : IClipboardService
    {
        public System.Threading.Tasks.Task<bool> SetTextAsync(string? text) =>
            System.Threading.Tasks.Task.FromResult(true);
    }
}
