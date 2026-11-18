using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SevenStation.Control;

/// <summary>
/// 托盘图标直接画：32×32 深色圆角方块加一个白 π。
/// 别去读 exe 里那张 .ico，它是 PNG 压缩帧，转成 HICON 后是空白的，通知区域里看不见。
/// </summary>
internal static class TrayIcon
{
    public static ImageSource Load() => Drawn();

    private static ImageSource Drawn()
    {
        const int size = 32;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1d)),
                null,
                new Rect(1, 1, size - 2, size - 2),
                7,
                7);

            var pen = new Pen(new SolidColorBrush(Colors.White), 2.4);
            context.DrawLine(pen, new Point(10, 11), new Point(22, 11));
            context.DrawLine(pen, new Point(13, 11), new Point(13, 22));
            context.DrawLine(pen, new Point(19, 11), new Point(19, 22));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
