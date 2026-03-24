using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Snap.Converters;

/// <summary>
/// Converts FrequencyLevel (0-10) to a LinearGradientBrush for the usage indicator bar.
/// </summary>
public class FrequencyLevelToBrushConverter : IValueConverter
{
    // Level → (width ratio, alpha)
    private static readonly (double Width, byte Alpha)[] Levels =
    [
        (0.00, 0x00),  // Level 0: none
        (0.10, 0x10),  // Level 1
        (0.18, 0x14),  // Level 2
        (0.26, 0x18),  // Level 3
        (0.34, 0x1C),  // Level 4
        (0.42, 0x22),  // Level 5
        (0.52, 0x28),  // Level 6
        (0.62, 0x30),  // Level 7
        (0.72, 0x38),  // Level 8
        (0.82, 0x42),  // Level 9
        (0.92, 0x4C),  // Level 10
    ];

    private static readonly Color BaseColor = Color.FromRgb(0x00, 0x99, 0xDD);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? Math.Clamp(i, 0, 10) : 0;
        if (level == 0)
            return Brushes.Transparent;

        var (width, alpha) = Levels[level];
        var color = Color.FromArgb(alpha, BaseColor.R, BaseColor.G, BaseColor.B);

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint = new System.Windows.Point(1, 0.5),
        };
        brush.GradientStops.Add(new GradientStop(color, 0.0));
        brush.GradientStops.Add(new GradientStop(color, width * 0.6));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, width));
        brush.Freeze();

        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
