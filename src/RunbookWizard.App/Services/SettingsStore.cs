using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RunbookWizard.App.Services;

/// <summary>User settings, kept in settings.json inside the data folder.</summary>
public sealed class AppSettings
{
    public string Theme { get; set; } = "System";
    public string? LogDirectory { get; set; }
    public string LogLevel { get; set; } = "Information";
    public List<string> Recent { get; set; } = new();
    public bool AutoAdvance { get; set; } = true;
    public string? UpdateManifestUrl { get; set; }
    public string? UpdateChannel { get; set; }
}

/// <summary>
/// Settings store. The data folder is <c>RUNBOOKWIZARD_DATA_DIR</c> (or <c>--data-dir</c>) when given, otherwise
/// <c>%LOCALAPPDATA%\RunbookWizard</c>. Nothing in it is a secret: guides, variable values and run output are never saved (ADR 0002).
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly string _file;

    public string Directory { get; }

    public SettingsStore(string[]? args = null)
    {
        var fromArgs = FindArgument(args, "--data-dir");
        var configured = fromArgs ?? Environment.GetEnvironmentVariable("RUNBOOKWIZARD_DATA_DIR");
        Directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunbookWizard")
            : Path.GetFullPath(configured);
        System.IO.Directory.CreateDirectory(Directory);
        _file = Path.Combine(Directory, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_file), Options) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(settings, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.For("settings").Warning(ex, "Settings could not be saved");
        }
    }

    private static string? FindArgument(string[]? args, string name)
    {
        if (args is null)
            return null;
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
