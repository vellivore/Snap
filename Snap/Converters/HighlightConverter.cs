using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Snap.Models;

namespace Snap.Converters;

public class HighlightConverter : IValueConverter
{
    public static readonly HighlightConverter Instance = new();

    private static readonly SolidColorBrush HighlightBrush = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0xE4, 0xE4, 0xE8));

    static HighlightConverter()
    {
        HighlightBrush.Freeze();
        NormalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not List<HighlightSegment> segments || segments.Count == 0)
            return new TextBlock { Text = "", Foreground = NormalBrush };

        var tb = new TextBlock();
        foreach (var seg in segments)
        {
            var run = new Run(seg.Text);
            if (seg.IsHighlight)
            {
                run.Foreground = HighlightBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                run.Foreground = NormalBrush;
            }
            tb.Inlines.Add(run);
        }
        return tb;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public class SecondaryHighlightConverter : IValueConverter
{
    public static readonly SecondaryHighlightConverter Instance = new();

    private static readonly SolidColorBrush HighlightBrush = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush NormalBrush = new(Color.FromRgb(0x88, 0x88, 0x90));

    static SecondaryHighlightConverter()
    {
        HighlightBrush.Freeze();
        NormalBrush.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not List<HighlightSegment> segments || segments.Count == 0)
            return new TextBlock { Text = "", Foreground = NormalBrush };

        var tb = new TextBlock();
        foreach (var seg in segments)
        {
            var run = new Run(seg.Text);
            if (seg.IsHighlight)
            {
                run.Foreground = HighlightBrush;
                run.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                run.Foreground = NormalBrush;
            }
            tb.Inlines.Add(run);
        }
        return tb;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
