using System.Text.RegularExpressions;
using RunbookWizard.Core.Model;

namespace RunbookWizard.Core.Execution;

/// <summary>
/// The values of a run: parameters entered by the person, values captured from step output and the built-ins
/// (<c>os</c>, <c>runbookDir</c>, <c>user</c>). Secret values are returned masked for previews and logs.
/// </summary>
public sealed class VariableStore
{
    public const string Mask = "••••••";

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Variable> _declared = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _builtins;

    public VariableStore(Runbook runbook, string? runbookDirectory = null)
    {
        foreach (var variable in runbook.Variables)
        {
            _declared[variable.Name] = variable;
            if (variable.Default is not null)
                _values[variable.Name] = variable.Default;
        }

        _builtins = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["os"] = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            ["runbookDir"] = runbookDirectory ?? Environment.CurrentDirectory,
            ["user"] = Environment.UserName,
        };
    }

    public IEnumerable<Variable> Declared => _declared.Values;

    public bool IsSecret(string name) => _declared.TryGetValue(name, out var v) && v.Type == VariableType.Secret;

    public void Set(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            _values.Remove(name);
        else
            _values[name] = value;
    }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v
        : _builtins.TryGetValue(name, out var b) ? b
        : null;

    /// <summary>Value for execution: a declared but empty variable is an empty string, an unknown name is null.</summary>
    public string? Resolve(string name) => Get(name) ?? (_declared.ContainsKey(name) ? string.Empty : null);

    /// <summary>Value for display: secrets are masked.</summary>
    public string? ResolveMasked(string name)
    {
        var value = Resolve(name);
        return value is { Length: > 0 } && IsSecret(name) ? Mask : value;
    }

    /// <summary>Validates one entered value against its declaration; returns an error text or null.</summary>
    public string? Validate(Variable variable)
    {
        var value = Get(variable.Name);
        if (string.IsNullOrEmpty(value))
            return variable.Required ? $"{variable.DisplayName} is required." : null;

        switch (variable.Type)
        {
            case VariableType.Number when !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _):
                return $"{variable.DisplayName} must be a number.";
            case VariableType.Bool when !bool.TryParse(value, out _):
                return $"{variable.DisplayName} must be true or false.";
            case VariableType.Choice when !variable.Choices.Contains(value, StringComparer.Ordinal):
                return $"{variable.DisplayName} must be one of: {string.Join(", ", variable.Choices)}.";
        }

        if (!string.IsNullOrEmpty(variable.Pattern))
        {
            try
            {
                if (!Regex.IsMatch(value, "^(?:" + variable.Pattern + ")$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                    return $"{variable.DisplayName} does not match the required pattern {variable.Pattern}.";
            }
            catch (ArgumentException)
            {
                return $"{variable.DisplayName}: the guide's pattern is not a valid regular expression.";
            }
        }
        return null;
    }

    /// <summary>All problems with entered values, for the variables a set of texts refers to (or all, when none given).</summary>
    public IReadOnlyList<string> ValidateAll(IEnumerable<string>? onlyNames = null)
    {
        var names = onlyNames?.ToHashSet(StringComparer.Ordinal);
        return _declared.Values
            .Where(v => names is null || names.Contains(v.Name))
            .Select(Validate)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
    }

    /// <summary>Replaces the value of every secret variable in a piece of text (script output, log lines) by the mask.</summary>
    public string MaskSecrets(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        foreach (var (name, value) in _values)
        {
            if (value.Length > 0 && IsSecret(name))
                text = text.Replace(value, Mask, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>The environment the process gets: <c>RUNBOOK_VAR_&lt;name&gt;</c> for every variable that has a value.</summary>
    public Dictionary<string, string> ToEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in _values)
            env["RUNBOOK_VAR_" + name] = value;
        foreach (var (name, value) in _builtins)
            env["RUNBOOK_" + name.ToUpperInvariant()] = value;
        return env;
    }
}
