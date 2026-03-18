using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SpotDesk.Core.Models;

namespace SpotDesk.UI.Converters;

/// <summary>Converts tree depth (int) to a left-indent Thickness. ConverterParameter = pixels per level.</summary>
public sealed class DepthIndentConverter : IValueConverter
{
    public static readonly DepthIndentConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var depth  = value is int d ? d : 0;
        var pixels = parameter is string s && int.TryParse(s, out var p) ? p : 14;
        return new Thickness(depth * pixels, 2, 0, 2);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>Maps Protocol enum to a colored brush for the sidebar badge background.</summary>
public sealed class ProtocolColorConverter : IValueConverter
{
    public static readonly ProtocolColorConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Protocol p ? p switch
        {
            Protocol.Rdp => new SolidColorBrush(Color.Parse("#2563EB")),
            Protocol.Ssh => new SolidColorBrush(Color.Parse("#16A34A")),
            Protocol.Vnc => new SolidColorBrush(Color.Parse("#9333EA")),
            _            => new SolidColorBrush(Color.Parse("#6B7280"))
        } : null;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}

/// <summary>Maps Protocol enum to a short display label with icon character.</summary>
public sealed class ProtocolLabelConverter : IValueConverter
{
    public static readonly ProtocolLabelConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Protocol p ? p switch
        {
            Protocol.Rdp => "RDP",
            Protocol.Ssh => "SSH",
            Protocol.Vnc => "VNC",
            _            => p.ToString()
        } : string.Empty;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Avalonia.Data.BindingOperations.DoNothing;
}
