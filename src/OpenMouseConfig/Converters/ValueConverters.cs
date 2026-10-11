using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OpenMouseConfig.Core.Diagnostics;
using OpenMouseConfig.ViewModels;

namespace OpenMouseConfig.Converters;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colour of the status dot in the header.</summary>
public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ThemeBrush(value switch
        {
            ConnectionState.Connected => "Brush.Success",
            ConnectionState.Connecting => "Brush.Warning",
            ConnectionState.Offline => "Brush.Accent",
            ConnectionState.Error => "Brush.Danger",
            _ => "Brush.TextMuted",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static Brush ThemeBrush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

/// <summary>Colour of the TX/RX/WARN/ERR tag in the console.</summary>
public sealed class LogKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ConnectionStateToBrushConverter.ThemeBrush(value switch
        {
            LogKind.Tx => "Brush.Accent",
            LogKind.Rx => "Brush.Info",
            LogKind.Success => "Brush.Success",
            LogKind.Warning => "Brush.Warning",
            LogKind.Error => "Brush.Danger",
            _ => "Brush.TextMuted",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Battery percentage (int?) → fill width; ConverterParameter = width at 100 %.</summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double full = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 100;
        return value is int percent ? full * Math.Clamp(percent, 0, 100) / 100.0 : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Battery percentage → red / amber / green.</summary>
public sealed class BatteryLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        ConnectionStateToBrushConverter.ThemeBrush(value switch
        {
            int p when p <= 15 => "Brush.Danger",
            int p when p <= 35 => "Brush.Warning",
            int => "Brush.Success",
            _ => "Brush.TextMuted",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
