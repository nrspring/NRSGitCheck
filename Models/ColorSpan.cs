namespace NRSGitCheck.Models;

/// <summary>
/// A run of characters within a line sharing a syntax-highlight foreground color
/// (FR-20). <see cref="Foreground"/> is a hex color string (e.g. <c>#D4D4D4</c>),
/// or null to use the default text color. Kept UI-framework-free.
/// </summary>
public sealed record ColorSpan(int Start, int Length, string? Foreground);
