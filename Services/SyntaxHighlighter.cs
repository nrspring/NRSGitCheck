using System;
using System.Collections.Generic;
using System.IO;
using NRSGitCheck.Models;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace NRSGitCheck.Services;

/// <summary>
/// TextMateSharp-backed syntax highlighter (FR-20). Resolves a grammar by file
/// extension and tokenizes line-by-line, carrying grammar state across lines, then
/// maps each token's scopes to a theme foreground color. Theme follows the app's
/// light/dark mode.
/// </summary>
public sealed class SyntaxHighlighter : ISyntaxHighlighter
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IGrammar?> _grammarCache = new(StringComparer.OrdinalIgnoreCase);

    private RegistryOptions _options;
    private Registry _registry;
    private Theme _theme;
    private bool _isDark = true;

    public SyntaxHighlighter()
    {
        _options = new RegistryOptions(ThemeName.DarkPlus);
        _registry = new Registry(_options);
        _theme = _registry.GetTheme();
    }

    public void SetDark(bool isDark)
    {
        lock (_gate)
        {
            if (isDark == _isDark && _registry is not null)
                return;

            _isDark = isDark;
            _options = new RegistryOptions(isDark ? ThemeName.DarkPlus : ThemeName.LightPlus);
            _registry = new Registry(_options);
            _theme = _registry.GetTheme();
            _grammarCache.Clear();
        }
    }

    public IReadOnlyList<IReadOnlyList<ColorSpan>>? Highlight(string filePath, string text)
    {
        lock (_gate)
        {
            var grammar = ResolveGrammar(filePath);
            if (grammar is null)
                return null;

            var lines = DiffEngine.SplitLines(text);
            var result = new List<IReadOnlyList<ColorSpan>>(lines.Length);
            IStateStack? ruleStack = null;

            foreach (var line in lines)
            {
                var tokenized = grammar.TokenizeLine(line, ruleStack, TimeSpan.MaxValue);
                ruleStack = tokenized.RuleStack;

                var spans = new List<ColorSpan>();
                foreach (var token in tokenized.Tokens)
                {
                    var start = Math.Min(token.StartIndex, line.Length);
                    var end = Math.Min(token.EndIndex, line.Length);
                    if (end <= start)
                        continue;

                    var foreground = -1;
                    foreach (var rule in _theme.Match(token.Scopes))
                    {
                        if (rule.foreground > 0)
                        {
                            foreground = rule.foreground;
                            break;
                        }
                    }

                    var hex = foreground > 0 ? _theme.GetColor(foreground) : null;
                    spans.Add(new ColorSpan(start, end - start, hex));
                }

                result.Add(spans);
            }

            return result;
        }
    }

    private IGrammar? ResolveGrammar(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return null;

        if (_grammarCache.TryGetValue(ext, out var cached))
            return cached;

        IGrammar? grammar = null;
        var language = _options.GetLanguageByExtension(ext);
        if (language is not null)
        {
            var scope = _options.GetScopeByLanguageId(language.Id);
            if (!string.IsNullOrEmpty(scope))
                grammar = _registry.LoadGrammar(scope);
        }

        _grammarCache[ext] = grammar;
        return grammar;
    }
}

/// <summary>No-op highlighter used as a fallback and in tests.</summary>
public sealed class NullSyntaxHighlighter : ISyntaxHighlighter
{
    public IReadOnlyList<IReadOnlyList<ColorSpan>>? Highlight(string filePath, string text) => null;
    public void SetDark(bool isDark) { }
}
