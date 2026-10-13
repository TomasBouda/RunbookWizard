using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunbookWizard.Core.Model;

public enum StepType { Info, Manual, Script }

/// <summary>Shell names of the format. <c>Powershell</c> prefers PowerShell 7 and falls back to Windows PowerShell.</summary>
public enum ShellKind { Powershell, Pwsh, Winps, Bash, Sh, Cmd }

public enum VariableType { String, Number, Bool, Choice, Path, Secret }

public enum PlatformKind { Windows, Linux, Macos }

/// <summary>A parameter the person enters once and the scripts use as <c>{{Name}}</c>.</summary>
public sealed class Variable
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string? Description { get; init; }
    public VariableType Type { get; init; } = VariableType.String;

    [JsonConverter(typeof(ScalarStringConverter))]
    public string? Default { get; init; }

    public bool Required { get; init; }
    public List<string> Choices { get; init; } = new();
    public string? Pattern { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? Name : Label!;
}

/// <summary>How a finished script is judged. All given rules must hold.</summary>
public sealed class SuccessRule
{
    public List<int> ExitCodes { get; init; } = new();
    public string? OutputMatches { get; init; }
    public string? OutputNotMatches { get; init; }

    public bool IsEmpty => ExitCodes.Count == 0 && OutputMatches is null && OutputNotMatches is null;
}

/// <summary>Stores part of a step's output in a variable for later steps.</summary>
public sealed class Capture
{
    public string Variable { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
}

public sealed class Step
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public StepType? Type { get; init; }
    public string? Description { get; init; }
    public string? Why { get; init; }
    public string? Confirm { get; init; }
    public ShellKind? Shell { get; init; }
    public string? Script { get; init; }
    public string? WorkingDirectory { get; init; }
    public int? TimeoutSeconds { get; init; }
    public Dictionary<string, string> Env { get; init; } = new();
    public bool Destructive { get; init; }
    public string? Warning { get; init; }
    public bool Elevated { get; init; }
    public string? When { get; init; }
    public SuccessRule? Success { get; init; }
    public List<Capture> Captures { get; init; } = new();
    public bool ContinueOnFailure { get; init; }

    /// <summary>The effective type: explicit, else script when a script is present, else info.</summary>
    [JsonIgnore]
    public StepType EffectiveType => Type ?? (string.IsNullOrWhiteSpace(Script) ? StepType.Info : StepType.Script);
}

public sealed class Runbook
{
    [JsonPropertyName("runbook")]
    public int FormatVersion { get; init; } = 1;

    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public List<string> Tags { get; init; } = new();
    public List<PlatformKind> Platforms { get; init; } = new();
    public ShellKind? Shell { get; init; }
    public List<Variable> Variables { get; init; } = new();
    public List<Step> Steps { get; init; } = new();

    /// <summary>The shell a step runs in: its own, else the guide's, else PowerShell.</summary>
    public ShellKind ShellOf(Step step) => step.Shell ?? Shell ?? ShellKind.Powershell;
}

/// <summary>Reads a JSON string, number or boolean as text (variable defaults may be written any of those ways).</summary>
public sealed class ScalarStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        JsonTokenType.Null => null,
        _ => throw new JsonException("Expected a string, number or boolean"),
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
