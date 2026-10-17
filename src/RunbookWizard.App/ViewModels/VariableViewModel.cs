using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using RunbookWizard.Core.Execution;
using RunbookWizard.Core.Model;

namespace RunbookWizard.App.ViewModels;

/// <summary>One parameter of the guide on the Parameters page. The value is entered once and lives in the shared <see cref="VariableStore"/>.</summary>
public sealed class VariableViewModel : ObservableObject
{
    private readonly VariableStore _store;
    private readonly Action _changed;
    private string _error;
    private string? _seen;

    public VariableViewModel(Variable model, VariableStore store, Action changed)
    {
        Model = model;
        _store = store;
        _changed = changed;
        Choices = model.Type == VariableType.Bool ? new List<string> { "true", "false" } : model.Choices;
        _error = store.Validate(model) ?? string.Empty;
        _seen = store.Get(model.Name);
    }

    public Variable Model { get; }

    public string Name => Model.Name;

    public string Label => Model.DisplayName;

    public string Placeholder => "{{" + Model.Name + "}}";

    public string? Description => Model.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Model.Description);

    public bool Required => Model.Required;

    public bool IsSecret => Model.Type == VariableType.Secret;

    public bool IsChoice => Model.Type is VariableType.Choice or VariableType.Bool;

    public bool IsText => !IsChoice;

    public char PasswordChar => IsSecret ? '•' : '\0';

    public string TypeLabel => Model.Type.ToString().ToLowerInvariant();

    public IReadOnlyList<string> Choices { get; }

    public string? Value
    {
        get => _store.Get(Model.Name);
        set
        {
            if (value == Value)
                return;
            _store.Set(Model.Name, value);
            _seen = _store.Get(Model.Name);
            FilledBy = null; // the person changed it (an agent's fill sets the mark again afterwards)
            OnPropertyChanged();
            Error = _store.Validate(Model) ?? string.Empty;
            _changed();
        }
    }

    private string? _filledBy;

    /// <summary>The agent whose value the field holds (ADR 0015); cleared as soon as the person edits it.</summary>
    public string? FilledBy
    {
        get => _filledBy;
        private set
        {
            if (SetProperty(ref _filledBy, value))
            {
                OnPropertyChanged(nameof(IsFilledByAgent));
                OnPropertyChanged(nameof(FilledByText));
            }
        }
    }

    public bool IsFilledByAgent => _filledBy is not null;

    public string FilledByText => _filledBy is null ? string.Empty : "Filled in by " + _filledBy;

    /// <summary>Sets the value on behalf of an agent and marks the field. Secret parameters never get here (the tool refuses them).</summary>
    internal void FillByAgent(string value, string agent)
    {
        Value = value;
        FilledBy = agent;
    }

    /// <summary>Tells the views that the stored value may have changed behind this object's back (a step captured it).</summary>
    public void Refresh()
    {
        if (string.Equals(_seen, _store.Get(Model.Name), StringComparison.Ordinal))
            return;
        _seen = _store.Get(Model.Name);
        RefreshCore();
    }

    private void RefreshCore()
    {
        OnPropertyChanged(nameof(Value));
        Error = _store.Validate(Model) ?? string.Empty;
    }

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;
}
