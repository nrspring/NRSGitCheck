using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>A selectable theme mode paired with its UI label (FR-28).</summary>
public sealed record ThemeOption(ThemeMode Mode, string Label);
