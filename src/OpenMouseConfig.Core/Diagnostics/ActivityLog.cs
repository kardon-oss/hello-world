namespace OpenMouseConfig.Core.Diagnostics;

/// <summary>Category of a log line. TX/RX lines carry raw HID traffic.</summary>
public enum LogKind
{
    Info,
    Success,
    Warning,
    Error,
    /// <summary>Host → device report (what we sent, or would have sent in dry-run).</summary>
    Tx,
    /// <summary>Device → host report.</summary>
    Rx,
}

/// <summary>
/// Sink for human-readable activity and raw packet traffic. The WPF app shows it in the
/// developer console; tests use <see cref="NullActivityLog"/> or a list-backed fake.
/// Implementations must be thread-safe: HID I/O runs on thread-pool threads.
/// </summary>
public interface IActivityLog
{
    void Write(LogKind kind, string message);
}

public static class ActivityLogExtensions
{
    public static void Info(this IActivityLog log, string message) => log.Write(LogKind.Info, message);
    public static void Success(this IActivityLog log, string message) => log.Write(LogKind.Success, message);
    public static void Warn(this IActivityLog log, string message) => log.Write(LogKind.Warning, message);
    public static void Error(this IActivityLog log, string message) => log.Write(LogKind.Error, message);
    public static void Tx(this IActivityLog log, string message) => log.Write(LogKind.Tx, message);
    public static void Rx(this IActivityLog log, string message) => log.Write(LogKind.Rx, message);
}

/// <summary>Discards everything.</summary>
public sealed class NullActivityLog : IActivityLog
{
    public static NullActivityLog Instance { get; } = new();

    public void Write(LogKind kind, string message)
    {
    }
}
