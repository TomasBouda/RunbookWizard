using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using RunbookWizard.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog.Events;

namespace RunbookWizard.App.ViewModels;

/// <summary>Logs page: the live ring buffer filtered by level and text, plus the log files on disk.</summary>
public sealed partial class LogsViewModel : ObservableObject
{
    public IReadOnlyList<string> Levels { get; } = new[] { "Debug", "Information", "Warning", "Error" };

    public ObservableCollection<LogLine> Lines { get; } = new();

    public ObservableCollection<FileInfo> Files { get; } = new();

    [ObservableProperty]
    private string _minimumLevel = "Information";

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private FileInfo? _selectedFile;

    [ObservableProperty]
    private string _tail = string.Empty;

    public string Directory => AppLog.Directory;

    public LogsViewModel()
    {
        AppLog.Recent.CollectionChanged += OnRecentChanged;
        Rebuild();
        ReloadFiles();
    }

    partial void OnMinimumLevelChanged(string value) => Rebuild();
    partial void OnFilterChanged(string value) => Rebuild();
    partial void OnSelectedFileChanged(FileInfo? value) => LoadTail();

    private void OnRecentChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
        {
            foreach (LogLine line in e.NewItems)
            {
                if (Matches(line))
                    Lines.Add(line);
            }
            while (Lines.Count > 2000)
                Lines.RemoveAt(0);
        }
        else
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in AppLog.Recent.Where(Matches))
            Lines.Add(line);
    }

    private bool Matches(LogLine line)
    {
        var min = Enum.TryParse<LogEventLevel>(MinimumLevel, true, out var parsed) ? parsed : LogEventLevel.Information;
        if (line.Level < min)
            return false;
        return string.IsNullOrWhiteSpace(Filter)
            || line.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase)
            || line.Source.Contains(Filter, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ReloadFiles()
    {
        var selected = SelectedFile?.FullName;
        Files.Clear();
        foreach (var file in AppLog.Files())
            Files.Add(file);
        SelectedFile = Files.FirstOrDefault(f => f.FullName == selected) ?? Files.FirstOrDefault();
        OnPropertyChanged(nameof(Directory));
    }

    /// <summary>Last ~200 lines of the selected file; the file is shared with the writer, so it is read without locking.</summary>
    private void LoadTail()
    {
        if (SelectedFile is null || !File.Exists(SelectedFile.FullName))
        {
            Tail = string.Empty;
            return;
        }
        try
        {
            using var stream = new FileStream(SelectedFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new LinkedList<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.AddLast(line);
                if (lines.Count > 200)
                    lines.RemoveFirst();
            }
            Tail = string.Join(Environment.NewLine, lines);
        }
        catch (IOException ex)
        {
            Tail = "Cannot read the file: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (System.IO.Directory.Exists(Directory))
            Process.Start(new ProcessStartInfo(Directory) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenFile()
    {
        if (SelectedFile is not null && File.Exists(SelectedFile.FullName))
            Process.Start(new ProcessStartInfo(SelectedFile.FullName) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Clear()
    {
        AppLog.Recent.Clear();
        Lines.Clear();
    }
}
