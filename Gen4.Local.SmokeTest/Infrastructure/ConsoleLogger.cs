using Microsoft.Extensions.Logging;

namespace Gen4.Local.SmokeTest.Infrastructure;

public sealed class ConsoleLogger
{
    private readonly object sync = new();

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Info(string format, params object?[] args)
    {
        Info(string.Format(format, args));
    }

    public void Warn(string message)
    {
        Write("WARN", message);
    }

    public void Warn(string format, params object?[] args)
    {
        Warn(string.Format(format, args));
    }

    public void Error(string message)
    {
        Write("ERROR", message);
    }

    public void Error(string format, params object?[] args)
    {
        Error(string.Format(format, args));
    }

    private void Write(string level, string message)
    {
        lock (sync)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level}] {message}");
        }
    }
}

public sealed class ConsoleLoggerProvider(ConsoleLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName)
    {
        return new Adapter(logger, categoryName);
    }

    public void Dispose()
    {
    }

    private sealed class Adapter(ConsoleLogger logger, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            logger.Info("[{0}] {1}", categoryName, formatter(state, exception));
        }
    }
}