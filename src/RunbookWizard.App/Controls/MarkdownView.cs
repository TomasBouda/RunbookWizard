using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Markdown.Avalonia;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.Controls;

/// <summary>
/// Renders a Markdown string (bold, italic, inline code, lists, links, headings, fenced code blocks with syntax highlighting).
/// A thin wrapper over the Markdown.Avalonia engine (ADR 0007): it applies the ops-console styles (Styles/Markdown.axaml),
/// swaps the engine's code blocks for <see cref="CodeView"/> (one highlighter for blocks and the command preview), restricts
/// links to http(s) and sizes to its content, so the page's own scroll viewer scrolls. Bind <see cref="Markdown"/>.
/// Add the class <c>secondary</c> for the quieter text colour.
/// </summary>
public class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private readonly global::Markdown.Avalonia.Markdown _engine;

    public MarkdownView()
    {
        var plugins = new MdAvPlugins { HyperlinkCommand = new OpenLinkCommand() };
        _engine = new global::Markdown.Avalonia.Markdown { Plugins = plugins };
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(ContentControl);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != MarkdownProperty)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(Markdown))
            {
                Content = null;
                return;
            }

            var root = _engine.Transform(Markdown.Replace("\r\n", "\n"));
            // The styles sit on the rendered root itself: the engine's own styles live there too and would otherwise win.
            root.Styles.Add(new StyleInclude(new Uri("avares://RunbookWizard/")) { Source = new Uri("avares://RunbookWizard/Styles/Markdown.axaml") });
            if (Classes.Contains("secondary"))
                root.Classes.Add("md-secondary");
            root.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => ReplaceCodeBlocks(root), DispatcherPriority.Loaded);
            Content = root;
        }
        catch (Exception ex)
        {
            // A broken description must not take the page down: show it as plain text instead.
            AppLog.For("ui").Warning(ex, "Markdown could not be rendered; showing plain text");
            Content = new SelectableTextBlock { Text = Markdown, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        }
    }

    /// <summary>Replaces the engine's code blocks (a plain editor plus a language label) with a highlighting <see cref="CodeView"/>.</summary>
    private static void ReplaceCodeBlocks(Control root)
    {
        foreach (var border in root.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("CodeBlock")).ToList())
        {
            if (border.Child is CodeView || border.GetVisualDescendants().OfType<TextEditor>().FirstOrDefault() is not { } editor)
                continue;
            var language = border.GetVisualDescendants().OfType<Label>().FirstOrDefault(l => l.Classes.Contains("LangInfo"))?.Content as string;
            border.Child = new CodeView { Code = editor.Text, Language = language };
        }
    }

    /// <summary>Guides are untrusted input: only plain web links are opened, in the default browser.</summary>
    private sealed class OpenLinkCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => IsWebLink(parameter as string);

        public void Execute(object? parameter)
        {
            if (!IsWebLink(parameter as string))
                return;
            try
            {
                Process.Start(new ProcessStartInfo((string)parameter!) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.For("ui").Warning(ex, "Could not open link {Link}", parameter);
            }
        }

        private static bool IsWebLink(string? value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }
}
