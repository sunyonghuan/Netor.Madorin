using System.Text;
using Microsoft.Extensions.Logging;

namespace ReminderPlugin;

internal sealed class HourlyFileLoggerProvider : ILoggerProvider
{
	private readonly Dictionary<string, HourlyFileLogger> _loggers = new(StringComparer.Ordinal);
	private readonly object _syncRoot = new();
	private readonly string _logsDirectory;
	private readonly UTF8Encoding _encoding = new(false);

	public HourlyFileLoggerProvider(string logsDirectory)
	{
		_logsDirectory = logsDirectory;
		Directory.CreateDirectory(_logsDirectory);
	}

	public ILogger CreateLogger(string categoryName)
	{
		lock (_syncRoot)
		{
			if (_loggers.TryGetValue(categoryName, out var logger))
				return logger;

			logger = new HourlyFileLogger(categoryName, WriteEntry);
			_loggers[categoryName] = logger;
			return logger;
		}
	}

	internal sealed class HourlyFileLogger : ILogger
	{
		private readonly string _categoryName;
		private readonly Action<LogLevel, string, EventId, string, Exception?> _writeEntry;

		public HourlyFileLogger(string categoryName, Action<LogLevel, string, EventId, string, Exception?> writeEntry)
		{
			_categoryName = categoryName;
			_writeEntry = writeEntry;
		}

		public IDisposable BeginScope<TState>(TState state) where TState : notnull
		{
			return NullScope.Instance;
		}

		public bool IsEnabled(LogLevel logLevel)
		{
			return logLevel != LogLevel.None;
		}

		public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
		{
			if (!IsEnabled(logLevel))
				return;

			var message = formatter(state, exception);
			_writeEntry(logLevel, _categoryName, eventId, message, exception);
		}
	}

	internal sealed class NullScope : IDisposable
	{
		public static NullScope Instance { get; } = new();

		public void Dispose()
		{
		}
	}

	public void Dispose()
	{
		lock (_syncRoot)
		{
			_loggers.Clear();
		}
	}

	private void WriteEntry(LogLevel logLevel, string categoryName, EventId eventId, string message, Exception? exception)
	{
		if (string.IsNullOrWhiteSpace(message) && exception is null)
			return;

		var now = DateTimeOffset.Now;
		var filePath = Path.Combine(_logsDirectory, $"{now:yyyyMMdd-HH}.log");
		var content = BuildLogLine(now, logLevel, categoryName, eventId, message, exception);

		lock (_syncRoot)
		{
			Directory.CreateDirectory(_logsDirectory);
			File.AppendAllText(filePath, content, _encoding);
		}
	}

	private static string BuildLogLine(
		DateTimeOffset timestamp,
		LogLevel logLevel,
		string categoryName,
		EventId eventId,
		string message,
		Exception? exception)
	{
		var builder = new StringBuilder();
		builder.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"));
		builder.Append(" [");
		builder.Append(logLevel);
		builder.Append("] ");
		builder.Append(categoryName);

		if (eventId.Id != 0)
		{
			builder.Append(" (");
			builder.Append(eventId.Id);
			builder.Append(')');
		}

		if (!string.IsNullOrWhiteSpace(message))
		{
			builder.Append(" - ");
			builder.Append(message);
		}

		if (exception is not null)
		{
			builder.AppendLine();
			builder.Append(exception);
		}

		builder.AppendLine();
		return builder.ToString();
	}
}