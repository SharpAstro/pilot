using System.Text;
using Microsoft.Extensions.Logging;

namespace Pilot.Contracts;

/// <summary>Writes each ILogger line to a <see cref="RollingLog"/>: the GUI's gui.log and the node's node.log, whatever the console shows.</summary>
public sealed class FileLoggerProvider(RollingLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, log);

    public void Dispose()
    {
    }

    private sealed class FileLogger(string category, RollingLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{logLevel,-11} {category}: {formatter(state, exception)}";
            log.Write(exception is null ? text : $"{text}\n{exception}");
        }
    }
}

/// <summary>A <see cref="TextWriter"/> that writes to a <see cref="RollingLog"/>: what stands where a stderr writer was passed.</summary>
public sealed class LogWriter(RollingLog log) : TextWriter
{
    private readonly StringBuilder _pending = new();
    private readonly object _gate = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_gate)
        {
            if (value == '\n')
            {
                Flush();
            }
            else
            {
                _pending.Append(value);
            }
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }
        lock (_gate)
        {
            _pending.Append(value);
            if (value.Contains('\n'))
            {
                Flush();
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            _pending.Append(value);
            Flush();
        }
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override void Flush()
    {
        lock (_gate)
        {
            if (_pending.Length > 0)
            {
                log.Write(_pending.ToString());
                _pending.Clear();
            }
        }
    }
}
