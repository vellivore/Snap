using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// WPF vector rendering for high-quality icon generation

int[] sizes = [256, 128, 64, 48, 32, 16];
var pngFiles = new List<(int size, string path)>();

foreach (var sz in sizes)
{
    // Render at 4x size with WPF vector engine, then let RenderTargetBitmap produce exact pixels
    int renderSize = sz * 4;
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(4, 4));
        DrawIcon(dc, sz);
        dc.Pop();
    }

    var rtb = new RenderTargetBitmap(renderSize, renderSize, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(visual);

    // High quality downscale using WPF
    var scaled = new TransformedBitmap(rtb, new ScaleTransform(0.25, 0.25));
    var final = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
    // Force render
    var wb = new WriteableBitmap(final);
    rtb = new RenderTargetBitmap(sz, sz, 96, 96, PixelFormats.Pbgra32);
    var drawVisual = new DrawingVisual();
    using (var dc2 = drawVisual.RenderOpen())
    {
        dc2.DrawImage(wb, new Rect(0, 0, sz, sz));
    }
    rtb.Render(drawVisual);

    var pngPath = Path.Combine("output", $"icon_{sz}.png");
    Directory.CreateDirectory("output");
    using var fs = new FileStream(pngPath, FileMode.Create);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(rtb));
    encoder.Save(fs);
    pngFiles.Add((sz, pngPath));
    Console.WriteLine($"Generated {sz}x{sz}");
}

// Combine PNGs into ICO
CreateIco(pngFiles, Path.Combine("output", "app.ico"));
Console.WriteLine("ICO created: output/app.ico");

static void DrawIcon(DrawingContext dc, double sz)
{
    double m = sz * 0.06;
    double r = sz * 0.16;

    // Background rounded rect
    var bgRect = new Rect(m, m, sz - m * 2, sz - m * 2);
    var bgBrush = new LinearGradientBrush(
        Color.FromRgb(0x28, 0x28, 0x28),
        Color.FromRgb(0x18, 0x18, 0x18),
        90);
    dc.DrawRoundedRectangle(bgBrush, null, bgRect, r, r);

    // Folder dimensions
    double fx = sz * 0.15, fy = sz * 0.24;
    double fw = sz * 0.72, fh = sz * 0.54;
    double tabW = fw * 0.36, tabH = fh * 0.22;
    double bodyY = fy + tabH * 0.5;

    var folderBrush = new LinearGradientBrush(
        Color.FromRgb(0x1E, 0x96, 0xF0),
        Color.FromRgb(0x08, 0x6C, 0xC8),
        90);

    // Folder tab
    var tabGeometry = new RectangleGeometry(new Rect(fx, fy, tabW, tabH), sz * 0.025, sz * 0.025);
    dc.DrawGeometry(folderBrush, null, tabGeometry);

    // Folder body
    var bodyGeometry = new RectangleGeometry(new Rect(fx, bodyY, fw, fh - tabH * 0.5), sz * 0.035, sz * 0.035);
    dc.DrawGeometry(folderBrush, null, bodyGeometry);

    // Folder top highlight
    var highlightBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
    dc.DrawRoundedRectangle(highlightBrush, null,
        new Rect(fx, bodyY, fw, sz * 0.02), sz * 0.01, sz * 0.01);

    // Lightning bolt
    double cx = sz * 0.52, cy = sz * 0.54;
    double s = sz * 0.17;

    var boltFigure = new PathFigure(new Point(cx - s * 0.35, cy - s * 1.15), new PathSegment[]
    {
        new LineSegment(new Point(cx + s * 0.65, cy - s * 1.15), true),
        new LineSegment(new Point(cx + s * 0.0,  cy - s * 0.05), true),
        new LineSegment(new Point(cx + s * 0.75, cy - s * 0.05), true),
        new LineSegment(new Point(cx - s * 0.25, cy + s * 1.15), true),
        new LineSegment(new Point(cx + s * 0.15, cy + s * 0.15), true),
        new LineSegment(new Point(cx - s * 0.45, cy + s * 0.15), true),
    }, true);

    var boltGeometry = new PathGeometry(new[] { boltFigure });

    // Drop shadow
    dc.PushTransform(new TranslateTransform(sz * 0.005, sz * 0.008));
    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, boltGeometry);
    dc.Pop();

    // White bolt with subtle gradient
    var boltBrush = new LinearGradientBrush(
        Color.FromRgb(0xFF, 0xFF, 0xFF),
        Color.FromRgb(0xE8, 0xE8, 0xE8),
        90);
    dc.DrawGeometry(boltBrush, null, boltGeometry);
}

static void CreateIco(List<(int size, string path)> pngs, string outPath)
{
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    // ICO header
    bw.Write((short)0);     // reserved
    bw.Write((short)1);     // ICO type
    bw.Write((short)pngs.Count);

    int headerSize = 6 + pngs.Count * 16;
    var pngDatas = new List<byte[]>();

    foreach (var (size, path) in pngs)
    {
        pngDatas.Add(File.ReadAllBytes(path));
    }

    int offset = headerSize;
    for (int i = 0; i < pngs.Count; i++)
    {
        var sz = pngs[i].size;
        var data = pngDatas[i];
        bw.Write((byte)(sz >= 256 ? 0 : sz));  // width
        bw.Write((byte)(sz >= 256 ? 0 : sz));  // height
        bw.Write((byte)0);    // palette
        bw.Write((byte)0);    // reserved
        bw.Write((short)1);   // color planes
        bw.Write((short)32);  // bits per pixel
        bw.Write(data.Length); // size
        bw.Write(offset);     // offset
        offset += data.Length;
    }

    foreach (var data in pngDatas)
    {
        bw.Write(data);
    }

    File.WriteAllBytes(outPath, ms.ToArray());
}
