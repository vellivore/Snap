using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snap.Helpers;

public sealed class DragGhostHelper
{
    private Window? _ghost;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>
    /// タブ風のゴーストウィンドウを表示する（アイコン + テキスト）
    /// </summary>
    public void Show(string text, ImageSource? icon = null)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        if (icon != null)
        {
            stack.Children.Add(new Image
            {
                Source = icon,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD4)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
        });

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x24, 0x24, 0x30)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Child = stack,
        };

        _ghost = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            IsHitTestVisible = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = border,
        };

        if (GetCursorPos(out var pt))
        {
            _ghost.Left = pt.X + 16;
            _ghost.Top = pt.Y + 8;
        }

        _ghost.Show();
    }

    public void UpdatePosition()
    {
        if (_ghost == null) return;
        if (GetCursorPos(out var pt))
        {
            _ghost.Left = pt.X + 16;
            _ghost.Top = pt.Y + 8;
        }
    }

    public void Close()
    {
        if (_ghost != null)
        {
            _ghost.Close();
            _ghost = null;
        }
    }
}
