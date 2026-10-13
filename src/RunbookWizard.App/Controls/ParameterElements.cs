using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using RunbookWizard.App.ViewModels;

namespace RunbookWizard.App.Controls;

/// <summary>
/// Turns the <c>{{Name}}</c> placeholders of the command in a <see cref="CodeView"/> into input fields inside the text: the rest of the
/// command stays read-only and highlighted. <c>{{!Name}}</c> (an escaped placeholder) is shown as the literal <c>{{Name}}</c>.
/// Only names the view was given a parameter for become fields.
/// </summary>
internal sealed partial class ParameterElementGenerator : VisualLineElementGenerator
{
    [GeneratedRegex(@"\{\{(?<esc>!)?\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    private readonly Func<IReadOnlyDictionary<string, VariableViewModel>?> _parameters;

    public ParameterElementGenerator(Func<IReadOnlyDictionary<string, VariableViewModel>?> parameters) => _parameters = parameters;

    public override int GetFirstInterestedOffset(int startOffset)
    {
        var parameters = _parameters();
        if (parameters is null)
            return -1;

        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        var segment = CurrentContext.GetText(startOffset, end - startOffset);
        var text = segment.Text.Substring(segment.Offset, segment.Count);
        foreach (Match match in Placeholder().Matches(text))
        {
            if (match.Groups["esc"].Success || parameters.ContainsKey(match.Groups["name"].Value))
                return startOffset + match.Index;
        }
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        var parameters = _parameters();
        if (parameters is null)
            return null;

        var end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        var segment = CurrentContext.GetText(offset, end - offset);
        var text = segment.Text.Substring(segment.Offset, segment.Count);
        var match = Placeholder().Match(text);
        if (!match.Success || match.Index != 0)
            return null;

        if (match.Groups["esc"].Success)
            return new FormattedTextElement(match.Value.Remove(2, 1), match.Length);
        return parameters.TryGetValue(match.Groups["name"].Value, out var parameter)
            ? new InlineObjectElement(match.Length, ParameterField.Create(parameter))
            : null;
    }
}

/// <summary>The input an inline placeholder turns into: a compact text box (masked for secrets) or a drop-down for choices and booleans.</summary>
internal static class ParameterField
{
    private const double CharWidth = 7.4;

    /// <summary>The field edits the same <see cref="VariableViewModel"/> as the Parameters page, so both always agree.</summary>
    public static Control Create(VariableViewModel parameter)
    {
        Control field = parameter.IsChoice ? CreateChoice(parameter) : CreateText(parameter);
        field.Name = "Param_" + parameter.Name;
        field.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        field.Margin = new Thickness(1, 0, 1, 4);
        field.Classes.Add("param");
        field.Classes.Set("invalid", parameter.HasError);
        ToolTip.SetTip(field, TipOf(parameter));

        // The editor behind the field starts a text selection (and takes the focus) on a press; a press on the field is the field's own.
        field.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => field.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        // The editor's TextArea takes the focus back when a GotFocus bubbles up to it; keep the event with the field.
        field.GotFocus += (_, e) => e.Handled = true;

        // Show validation as it changes; unhook when the line is rebuilt and the field leaves the tree.
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(VariableViewModel.Error) or nameof(VariableViewModel.HasError))
            {
                field.Classes.Set("invalid", parameter.HasError);
                ToolTip.SetTip(field, TipOf(parameter));
            }
        }
        field.AttachedToVisualTree += (_, _) => parameter.PropertyChanged += OnChanged;
        field.DetachedFromVisualTree += (_, _) => parameter.PropertyChanged -= OnChanged;
        return field;
    }

    private static string TipOf(VariableViewModel parameter)
    {
        var tip = parameter.Label + " (" + parameter.Placeholder + ")";
        if (parameter.HasError)
            tip += "\n" + parameter.Error;
        else if (parameter.IsSecret)
            tip += "\nMasked here, in the preview, in the output and in the log.";
        return tip;
    }

    private static Control CreateText(VariableViewModel parameter)
    {
        var length = parameter.IsSecret ? 0 : (parameter.Value?.Length ?? 0);
        var chars = Math.Clamp(Math.Max(length, parameter.Name.Length) + 2, 8, 44);
        var box = new TextBox
        {
            Width = chars * CharWidth + 14,
            Watermark = parameter.Name,
            PasswordChar = parameter.PasswordChar,
            AcceptsReturn = false,
        };
        box.Bind(TextBox.TextProperty, new Binding(nameof(VariableViewModel.Value)) { Source = parameter, Mode = BindingMode.TwoWay });
        return box;
    }

    private static Control CreateChoice(VariableViewModel parameter)
    {
        var longest = 4;
        foreach (var choice in parameter.Choices)
            longest = Math.Max(longest, choice.Length);
        var combo = new ComboBox
        {
            ItemsSource = parameter.Choices,
            MinWidth = (longest + 2) * CharWidth + 28,
        };
        combo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(VariableViewModel.Value)) { Source = parameter, Mode = BindingMode.TwoWay });
        return combo;
    }
}
