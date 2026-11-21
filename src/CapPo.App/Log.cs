using Microsoft.Extensions.Logging;

namespace CapPo.App;

/// <summary>
/// CapPo's log: cappo.log in <see cref="AppInfo.DataDirectory"/>, on this PC only. Nothing is ever sent anywhere.
/// Kept small: it starts over past 512 KB.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock s_lock = new();

    public static string FilePath => Path.Combine(AppInfo.DataDirectory, "cappo.log");

    public static ILoggerFactory Factory { get; } = LoggerFactory.Create(builder => builder
        .SetMinimumLevel(LogLevel.Information)
        .AddProvider(new FileLoggerProvider()));

    private static readonly ILogger s_app = Factory.CreateLogger("CapPo");

    public static void Info(string message) => s_app.LogInformation("{Message}", message);

    public static void Error(Exception exception, string message) => s_app.LogError(exception, "{Message}", message);

    private static void Write(string line)
    {
        lock (s_lock)
        {
            try
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxBytes)
                    file.Delete();
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop CapPo.
            }
        }
    }

    private sealed class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..]);

        public void Dispose()
        {
        }
    }

    private sealed class FileLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var level = logLevel switch { LogLevel.Warning => "WARN", LogLevel.Error or LogLevel.Critical => "ERROR", _ => "info" };
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {level} {category}: {formatter(state, exception)}";
            Write(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }
    }
}
