using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ListHero.Tests.Diagnostics;

public sealed class DiagnosticLogs : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
    public ConcurrentQueue<Entry> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider provider) => scopes = provider;
    public void Dispose() { }
    public string Text => string.Join('\n', Entries.Select(entry => entry.Message + " " + string.Join(' ', entry.Scopes) + " " + entry.Exception));
    public ILoggerFactory Factory() => LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(this));
    public sealed record Entry(string Category, EventId Event, LogLevel Level, string Message, string[] Scopes, Exception? Exception);
    private sealed class Sink(DiagnosticLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var active = new List<string>();
            owner.scopes.ForEachScope((scope, list) => list.Add(scope is IEnumerable<KeyValuePair<string, object>> fields
                ? string.Join(' ', fields.Select(field => field.Key + "=" + field.Value)) : scope?.ToString() ?? ""), active);
            owner.Entries.Enqueue(new(category, eventId, level, formatter(state, exception), active.ToArray(), exception));
        }
    }
}
