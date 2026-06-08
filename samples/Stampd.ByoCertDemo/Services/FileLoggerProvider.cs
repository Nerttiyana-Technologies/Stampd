using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Stampd.ByoCertDemo.Services;

/// <summary>
/// Minimal append-only file logger. Writes every log line — irrespective of
/// category — to a single rolling .log file inside the demo's vault directory.
/// Adds the standard ASP.NET Core console logger's information by also logging
/// to console via the standard pipeline; this one just adds disk persistence.
/// </summary>
/// <remarks>
/// We deliberately don't pull in Serilog or NLog here because the demo aims to
/// stay dependency-light. This single tiny class handles file output. Each
/// process startup opens a new file (yyyy-MM-dd_HH-mm-ss.log) so successive
/// runs don't fight over the same handle and so a single demo session's
/// logs are easy to grep.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var fileName = $"demo-{DateTimeOffset.UtcNow:yyyy-MM-dd_HH-mm-ss}.log";
        var path = Path.Combine(directory, fileName);
        _writer = new StreamWriter(path, append: true, encoding: Encoding.UTF8)
        {
            AutoFlush = true,
        };
        FullPath = path;
    }

    /// <summary>Where the active log file lives — useful to print at boot.</summary>
    public string FullPath { get; }

    public ILogger CreateLogger(string categoryName) =>
        new FileLogger(categoryName, _writer, _gate);

    public void Dispose()
    {
        _writer.Flush();
        _writer.Dispose();
    }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly StreamWriter _writer;
        private readonly object _gate;

        public FileLogger(string category, StreamWriter writer, object gate)
        {
            _category = category;
            _writer = writer;
            _gate = gate;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            var sb = new StringBuilder(256);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            sb.Append(' ');
            sb.Append(LevelTag(logLevel));
            sb.Append(' ');
            sb.Append('[').Append(_category).Append("] ");
            sb.Append(message);
            if (exception is not null)
            {
                sb.AppendLine();
                sb.Append(exception);
            }

            lock (_gate)
            {
                _writer.WriteLine(sb.ToString());
            }
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace       => "TRCE",
            LogLevel.Debug       => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning     => "WARN",
            LogLevel.Error       => "FAIL",
            LogLevel.Critical    => "CRIT",
            _                    => "    ",
        };
    }
}
