using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using OpenMouseConfig.Core.Diagnostics;

namespace OpenMouseConfig.Services;

/// <summary>One line in the developer console.</summary>
public sealed record LogEntry(DateTime Timestamp, LogKind Kind, string Message)
{
    public string Time => Timestamp.ToString("HH:mm:ss.fff");

    public string Tag => Kind switch
    {
        LogKind.Tx => "TX",
        LogKind.Rx => "RX",
        LogKind.Warning => "WARN",
        LogKind.Error => "ERR",
        LogKind.Success => "OK",
        _ => "INFO",
    };

    public override string ToString() => $"{Time} {Tag,-4} {Message}";
}

/// <summary>
/// <see cref="IActivityLog"/> backed by an observable collection bound to the console.
/// Safe to call from any thread: entries are marshalled to the UI dispatcher.
/// </summary>
public sealed class UiActivityLog : IActivityLog
{
    private const int MaxEntries = 2000;

    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<LogEntry> _entries = [];

    public UiActivityLog(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        Entries = new ReadOnlyObservableCollection<LogEntry>(_entries);
    }

    public ReadOnlyObservableCollection<LogEntry> Entries { get; }

    public void Write(LogKind kind, string message)
    {
        var entry = new LogEntry(DateTime.Now, kind, message);
        Debug.WriteLine(entry);

        if (_dispatcher.CheckAccess())
        {
            Add(entry);
        }
        else
        {
            _dispatcher.BeginInvoke(() => Add(entry));
        }
    }

    public void Clear() => _entries.Clear();

    private void Add(LogEntry entry)
    {
        _entries.Add(entry);
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(0);
        }
    }
}
