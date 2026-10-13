using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using RunbookWizard.App.Services;

namespace RunbookWizard.App.Controls;

/// <summary>
/// Read-only, selectable, syntax-highlighted code (AvaloniaEdit): the command preview of a script step and the fenced blocks
/// of rendered Markdown. It grows with its content (no inner scrolling), wraps long lines and re-tones the grammar colours
/// when the theme changes. Bind <see cref="Code"/> and <see cref="Language"/> (a shell name or a fence tag; unknown stays plain).
/// </summary>
public class CodeView : TextEditor
{
    public static readonly StyledProperty<string?> CodeProperty = AvaloniaProperty.Register<CodeView, string?>(nameof(Code));
    public static readonly StyledProperty<string?> LanguageProperty = AvaloniaProperty.Register<CodeView, string?>(nameof(Language));

    public CodeView()
    {
        IsReadOnly = true;
        ShowLineNumbers = false;
        WordWrap = true;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        FontSize = 12;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.HighlightCurrentLine = false;
        TextArea.Caret.CaretBrush = Brushes.Transparent;
        TextArea.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Ibeam);
        this[!FontFamilyProperty] = this.GetResourceObservable("MonoFont").ToBinding();
        this[!ForegroundProperty] = this.GetResourceObservable("TextBrush").ToBinding();
    }

    public string? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CodeProperty)
            Text = Code ?? string.Empty;
        else if (change.Property == LanguageProperty)
            Highlight();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        Highlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Highlight();

    private void Highlight()
    {
        var definition = CodeHighlighting.Find(Language);
        if (definition is not null)
            CodeHighlighting.Apply(definition, CodeHighlighting.IsDark(ActualThemeVariant));
        SyntaxHighlighting = null;
        SyntaxHighlighting = definition;
        TextArea.TextView.Redraw();
    }
}
