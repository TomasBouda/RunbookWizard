using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RunbookWizard.App.Services;

/// <summary>One log event as shown on the Logs page.</summary>
public sealed record LogLine(DateTimeOffset Time, LogEventLevel Level, string Source, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???",
    };
    public bool IsWarning => Level == LogEventLevel.Warning;
    public bool IsError => Level >= LogEventLevel.Error;
}

/// <summary>
/// Application log: Serilog into daily rolling files plus an in-memory ring buffer the Logs page reads live.
/// Secrets never reach the log: script output is masked before it is logged, and variable values are not logged.
/// </summary>
public static class AppLog
{
    private const int BufferSize = 2000;

    public static ObservableCollection<LogLine> Recent { get; } = new();

    public static string Directory { get; private set; } = string.Empty;

    public static ILogger For(string source) => Log.ForContext("SourceContext", source);

    public static void Initialize(AppSettings settings, string dataDirectory)
    {
        Directory = string.IsNullOrWhiteSpace(settings.LogDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RunbookWizard", "logs")
            : Path.GetFullPath(settings.LogDirectory, dataDirectory); // relative = inside the data folder (fixtures, portable copies)
        System.IO.Directory.CreateDirectory(Directory);

        var level = Enum.TryParse<LogEventLevel>(settings.LogLevel, true, out var parsed) ? parsed : LogEventLevel.Information;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level < LogEventLevel.Information ? level : LogEventLevel.Debug)
            .Enrich.FromLogContext()
            .WriteTo.Sink(new BufferSink(), LogEventLevel.Debug)
            .WriteTo.File(
                Path.Combine(Directory, "runbookwizard-.log"),
                restrictedToMinimumLevel: level,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        For("app").Information("RunbookWizard {Version} starting, data in {Data}, logs in {Logs}", AppInfo.Version, dataDirectory, Directory);
    }

    public static void Shutdown()
    {
        For("app").Information("RunbookWizard exiting");
        Log.CloseAndFlush();
    }

    /// <summary>Log files on disk, newest first.</summary>
    public static IReadOnlyList<FileInfo> Files() =>
        System.IO.Directory.Exists(Directory)
            ? new DirectoryInfo(Directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).ToList()
            : Array.Empty<FileInfo>();

    private sealed class BufferSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            var source = logEvent.Properties.TryGetValue("SourceContext", out var s) ? s.ToString().Trim('"') : "app";
            var message = logEvent.RenderMessage();
            if (logEvent.Exception is not null)
                message += " – " + logEvent.Exception.Message;
            var line = new LogLine(logEvent.Timestamp, logEvent.Level, source, message);

            Dispatcher.UIThread.Post(() =>
            {
                Recent.Add(line);
                while (Recent.Count > BufferSize)
                    Recent.RemoveAt(0);
            });
        }
    }
}
