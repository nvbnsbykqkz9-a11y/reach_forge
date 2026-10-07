using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ReachForge.Desktop.Host;

/// <summary>
/// ログをデータフォルダーの logs に日付ごとのファイルで残す（Windows 版は画面のないプロセスのため）。14日より古いファイルは消す。
/// 書き込みは専用のスレッドでまとめて行い、アプリの処理を待たせない。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;
    public const string FilePrefix = "reachforge-";

    private readonly string _directory;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.GetFiles(directory, FilePrefix + "*.log")
                     .Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-RetentionDays)))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
            }
        }
        _writer = new Thread(Write) { IsBackground = true, Name = "ReachForge log writer" };
        _writer.Start();
    }

    /// <summary>今日のログファイル。</summary>
    public string CurrentFile => Path.Combine(_directory, $"{FilePrefix}{DateTime.Now:yyyyMMdd}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted) _queue.TryAdd(line);
    }

    private void Write()
    {
        StreamWriter? writer = null;
        var path = "";
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                if (CurrentFile != path)
                {
                    writer?.Dispose();
                    path = CurrentFile;
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false));
                }
                writer!.WriteLine(line);
                if (_queue.Count == 0) writer.Flush();
            }
            catch (IOException)
            {
                // ログを書けなくてもアプリは止めない
            }
        }
        writer?.Dispose();
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Level(logLevel)}] {category}: {formatter(state, exception)}";
            provider.Enqueue(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "none",
        };
    }
}
