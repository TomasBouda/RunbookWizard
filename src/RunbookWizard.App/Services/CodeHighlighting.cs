using System;
using System.Collections.Generic;
using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using RunbookWizard.Core.Execution;

namespace RunbookWizard.App.Services;

/// <summary>
/// Syntax grammars for code on screen (command preview, fenced blocks in Markdown), from AvaloniaEdit: its stock definitions
/// (PowerShell, JSON, XML, C#, ...) plus the two shell grammars in Assets/Highlighting (bash, batch). The stock grammars are
/// drawn for a light background; <see cref="Apply"/> re-tones their colours for the dark theme (ADR 0007).
/// </summary>
public static class CodeHighlighting
{
    private static readonly object Gate = new();
    private static bool _registered;
    private static readonly Dictionary<HighlightingColor, Color?> Originals = new(ReferenceEqualityComparer.Instance);

    /// <summary>The grammar for a language tag (see <see cref="CodeLanguage.Canonical"/>), or null when none is known: the code then stays plain.</summary>
    public static IHighlightingDefinition? Find(string? language)
    {
        var name = CodeLanguage.Canonical(language);
        if (name is null)
            return null;

        EnsureRegistered();
        var manager = HighlightingManager.Instance;
        var definition = name switch
        {
            CodeLanguage.PowerShell => manager.GetDefinition("PowerShell"),
            CodeLanguage.Bash => manager.GetDefinition("Bash"),
            CodeLanguage.Batch => manager.GetDefinition("Batch"),
            "csharp" or "cs" => manager.GetDefinition("C#"),
            "js" => manager.GetDefinition("JavaScript"),
            "md" => manager.GetDefinition("MarkDown"),
            "sql" => manager.GetDefinition("TSQL"),
            "cpp" or "c" => manager.GetDefinition("C++"),
            "py" => manager.GetDefinition("Python"),
            _ => manager.GetDefinition(name) ?? manager.GetDefinitionByExtension("." + name),
        };
        if (definition is not null)
            Apply(definition, IsDark(Application.Current?.ActualThemeVariant));
        return definition;
    }

    public static bool IsDark(Avalonia.Styling.ThemeVariant? variant) => variant == Avalonia.Styling.ThemeVariant.Dark;

    /// <summary>Sets every named colour of the grammar for the light or the dark theme. Safe to call repeatedly.</summary>
    public static void Apply(IHighlightingDefinition definition, bool dark)
    {
        lock (Gate)
        {
            foreach (var color in definition.NamedHighlightingColors)
            {
                if (!Originals.TryGetValue(color, out var original))
                {
                    original = (color.Foreground?.GetBrush(null) as ISolidColorBrush)?.Color;
                    Originals[color] = original;
                }

                if (original is not { } source)
                    continue;
                var tone = dark ? ForDark(source) : ForLight(source);
                color.Foreground = tone is { } c ? new SimpleHighlightingBrush(c) : null;
            }
        }
    }

    /// <summary>Black and white stay with the text colour of the theme; every other colour keeps its hue.</summary>
    internal static Color? ForLight(Color color)
    {
        var (_, s, l) = ToHsl(color);
        return s < 0.12 && l > 0.85 ? null : color;
    }

    /// <summary>The same hue, lifted to a lightness that reads on a dark editor background.</summary>
    internal static Color? ForDark(Color color)
    {
        var (h, s, l) = ToHsl(color);
        if (s < 0.12)
            return l < 0.45 ? null : color;
        return FromHsl(h, Math.Clamp(s, 0.4, 0.65), Math.Clamp(Math.Max(l, 0.7), 0.7, 0.8));
    }

    private static void EnsureRegistered()
    {
        lock (Gate)
        {
            if (_registered)
                return;
            _registered = true;
            Register("Bash", "avares://RunbookWizard/Assets/Highlighting/Bash.xshd", ".sh", ".bash");
            Register("Batch", "avares://RunbookWizard/Assets/Highlighting/Batch.xshd", ".bat", ".cmd");
        }
    }

    private static void Register(string name, string uri, params string[] extensions)
    {
        using var stream = AssetLoader.Open(new Uri(uri));
        using var reader = XmlReader.Create(stream);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(name, extensions, definition);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
            return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double Channel(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromRgb((byte)Math.Round(Channel(p, q, h + 1.0 / 3) * 255), (byte)Math.Round(Channel(p, q, h) * 255), (byte)Math.Round(Channel(p, q, h - 1.0 / 3) * 255));
    }
}
