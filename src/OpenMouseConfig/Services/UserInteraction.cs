using System.Diagnostics;
using System.IO;
using System.Windows;

namespace OpenMouseConfig.Services;

/// <summary>
/// Everything the view model needs from the shell (dialogs, clipboard, Explorer), behind an
/// interface so the view model stays free of WPF statics and remains testable.
/// </summary>
public interface IUserInteraction
{
    bool Confirm(string title, string message);

    void CopyToClipboard(string text);

    void OpenFolder(string path);
}

public sealed class WpfUserInteraction : IUserInteraction
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current.MainWindow, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            == MessageBoxResult.Yes;

    public void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard is locked by another process; ignore.
        }
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
