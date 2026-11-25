using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EmergencyDesk.Models;

namespace EmergencyDesk.Converters;

internal static class Brushes2
{
    public static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}

/// <summary>PriorityLevel -> badge background.</summary>
public class PriorityBrushConverter : IValueConverter
{
    private static readonly Brush Critical = Brushes2.Make(0xE5, 0x48, 0x4D);
    private static readonly Brush High = Brushes2.Make(0xF7, 0x6B, 0x15);
    private static readonly Brush Medium = Brushes2.Make(0xC9, 0x8A, 0x0B);
    private static readonly Brush Low = Brushes2.Make(0x4A, 0x5B, 0x6C);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PriorityLevel l ? l switch
        {
            PriorityLevel.Critical => Critical,
            PriorityLevel.High => High,
            PriorityLevel.Medium => Medium,
            _ => Low
        } : Low;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>SlaState -> text colour.</summary>
public class SlaBrushConverter : IValueConverter
{
    private static readonly Brush Ok = Brushes2.Make(0xE8, 0xED, 0xF2);
    private static readonly Brush Warn = Brushes2.Make(0xF5, 0xA5, 0x24);
    private static readonly Brush Bad = Brushes2.Make(0xFF, 0x6B, 0x6F);
    private static readonly Brush Good = Brushes2.Make(0x3D, 0xD6, 0x8C);
    private static readonly Brush Muted = Brushes2.Make(0x8A, 0x99, 0xA8);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SlaState s ? s switch
        {
            SlaState.Ok => Ok,
            SlaState.Warning => Warn,
            SlaState.Breached => Bad,
            SlaState.Late => Bad,
            SlaState.Met => Good,
            _ => Muted
        } : Muted;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Two-way "is this the selected one" converter for RadioButtons bound to a string property
/// (navigation, queue filter chips).
/// </summary>
public class EqualsParamConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}
