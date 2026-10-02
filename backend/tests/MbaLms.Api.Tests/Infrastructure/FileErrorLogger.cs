using Microsoft.Extensions.Logging;

namespace MbaLms.Api.Tests.Infrastructure;

/// <summary>Writes server-side errors of the test host to a file, so 500 responses can be diagnosed.</summary>
public sealed class FileErrorLoggerProvider(string path) : ILoggerProvider
{
    private readonly Lock _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileErrorLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string text)
    {
        lock (_lock) File.AppendAllText(path, text + Environment.NewLine);
    }

    private sealed class FileErrorLogger(FileErrorLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write($"[{DateTime.Now:HH:mm:ss}] {category}: {formatter(state, exception)}{Environment.NewLine}{exception}");
        }
    }
}
