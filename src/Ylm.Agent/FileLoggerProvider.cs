namespace Ylm.Agent;

/// <summary>Minimal rolling daily log file, so the tray's "Logs öffnen" has something to show.</summary>
public sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"agent-{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
                foreach (var old in Directory.GetFiles(directory, "agent-*.log").Where(f => File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14))) File.Delete(old);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
        }
    }
}
