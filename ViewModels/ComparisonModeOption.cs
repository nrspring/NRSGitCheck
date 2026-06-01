using NRSGitCheck.Models;

namespace NRSGitCheck.ViewModels;

/// <summary>A selectable comparison mode paired with its UI label (FR-7).</summary>
public sealed record ComparisonModeOption(ComparisonMode Mode, string Label);
