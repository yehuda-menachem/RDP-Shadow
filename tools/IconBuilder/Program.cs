using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IconBuilder;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.WriteLine("Generating optimized RDP Shadow icons...");

        int[] sizes = [256, 128, 64, 48, 32, 24, 16];
        var pngStreams = new (int size, byte[] data)[sizes.Length];

        string outputDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\src\RdpShadow.App\Assets"));
        Directory.CreateDirectory(outputDir);

        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            byte[] pngData = RenderIconToPng(size);
            pngStreams[i] = (size, pngData);

            string pngPath = Path.Combine(outputDir, $"icon_{size}.png");
            File.WriteAllBytes(pngPath, pngData);
            Console.WriteLine($"Generated: {pngPath}");
        }

        // Build multi-res .ico
        string icoPath = Path.Combine(outputDir, "app.ico");
        BuildIcoFile(pngStreams, icoPath);
        Console.WriteLine($"Successfully generated multi-resolution icon: {icoPath}");
    }

    static byte[] RenderIconToPng(int size)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            if (size == 16)
            {
                // Pixel-snapped 16x16 rendering for maximum clarity in taskbar & titlebar
                var bg = new SolidColorBrush(Color.FromRgb(14, 20, 34));
                var border = new SolidColorBrush(Color.FromArgb(160, 56, 189, 248));
                var frontPen = new Pen(new SolidColorBrush(Color.FromRgb(0, 230, 255)), 1.2);
                var rearPen = new Pen(new SolidColorBrush(Color.FromRgb(30, 95, 160)), 1.2);
                var boltBrush = Brushes.White;

                // Squircle container
                dc.DrawRoundedRectangle(bg, new Pen(border, 0.8), new Rect(0.5, 0.5, 15, 15), 3.5, 3.5);

                // Rear shadow screen
                dc.DrawRoundedRectangle(null, rearPen, new Rect(5.0, 5.5, 8.5, 6.5), 1.2, 1.2);

                // Front screen
                dc.DrawRoundedRectangle(null, frontPen, new Rect(2.0, 2.5, 8.5, 6.5), 1.2, 1.2);

                // Crisp lightning bolt
                var bolt16 = new StreamGeometry();
                using (var c = bolt16.Open())
                {
                    c.BeginFigure(new Point(6.8, 3.2), true, true);
                    c.LineTo(new Point(4.3, 5.8), true, false);
                    c.LineTo(new Point(6.0, 5.8), true, false);
                    c.LineTo(new Point(5.0, 8.4), true, false);
                    c.LineTo(new Point(8.0, 5.2), true, false);
                    c.LineTo(new Point(6.5, 5.2), true, false);
                }
                bolt16.Freeze();
                dc.DrawGeometry(boltBrush, null, bolt16);
            }
            else
            {
                // Vector scaling for >= 24px
                double scale = size / 512.0;
                dc.PushTransform(new ScaleTransform(scale, scale));

            // Background squircle gradient (Deep midnight obsidian)
            var bgGrad = new LinearGradientBrush(
                Color.FromRgb(16, 23, 38),   // #101726
                Color.FromRgb(6, 10, 18),    // #060A12
                new Point(0, 0),
                new Point(1, 1));

            // Squircle subtle highlight border
            var rimBrush = new LinearGradientBrush(
                Color.FromArgb(180, 56, 189, 248), // Sky cyan top-left
                Color.FromArgb(50, 30, 41, 59),    // Slate bottom-right
                new Point(0, 0),
                new Point(1, 1));

            // Rear Shadow Screen (Contrast tuned so it's unmistakably visible as a shadow monitor)
            Color rearColor1 = size <= 32 ? Color.FromRgb(24, 82, 140) : Color.FromRgb(18, 64, 110);
            Color rearColor2 = size <= 32 ? Color.FromRgb(14, 52, 92)  : Color.FromRgb(10, 40, 72);
            var rearBrush = new LinearGradientBrush(
                rearColor1,
                rearColor2,
                new Point(0, 0),
                new Point(1, 1));

            // Front Screen Gradient (Vivid Electric Cyan -> Azure Blue)
            var frontGrad = new LinearGradientBrush(
                Color.FromRgb(0, 245, 255),  // Vibrant Cyan
                Color.FromRgb(0, 120, 255),  // Royal Azure
                new Point(0, 0),
                new Point(1, 1));

            // Bolt Gradient (Pure White with subtle cool white undertone)
            var boltGrad = new LinearGradientBrush(
                Color.FromRgb(255, 255, 255),
                Color.FromRgb(235, 248, 255),
                new Point(0, 0),
                new Point(0.5, 1));

            // 1. Draw Squircle
            double squirclePad = size <= 32 ? 14 : 26;
            double squircleSize = 512 - (squirclePad * 2);
            double cornerRadius = size <= 32 ? 120 : 110;
            double rimThickness = size <= 32 ? 6 : 4;
            dc.DrawRoundedRectangle(bgGrad, new Pen(rimBrush, rimThickness),
                new Rect(squirclePad, squirclePad, squircleSize, squircleSize), cornerRadius, cornerRadius);

            // Screen Rect dimensions
            double screenW = size <= 32 ? 260 : 236;
            double screenH = size <= 32 ? 186 : 170;
            double screenRx = size <= 32 ? 34 : 30;

            // Offset parameters - perfectly centered in X and Y
            // Combined X span: frontX to frontX + shadowOffsetX + screenW
            // Midpoint X: frontX + (shadowOffsetX + screenW)/2 = 256 => frontX = 256 - (shadowOffsetX + screenW)/2
            double shadowOffsetX = size <= 32 ? 72 : 68;
            double shadowOffsetY = size <= 32 ? 58 : 54;
            double frontX = 256 - ((shadowOffsetX + screenW) / 2.0);
            double frontY = 256 - ((shadowOffsetY + screenH) / 2.0);

            Rect frontRect = new Rect(frontX, frontY, screenW, screenH);
            Rect rearRect = new Rect(frontX + shadowOffsetX, frontY + shadowOffsetY, screenW, screenH);

            // 2. Rear "Shadow" Screen Frame
            double rearStroke = size <= 32 ? 34 : 26;
            dc.DrawRoundedRectangle(null, new Pen(rearBrush, rearStroke), rearRect, screenRx, screenRx);

            // 3. Front "Primary" Screen Frame
            // Ambient Neon Glow around front screen
            if (size >= 48)
            {
                // Multi-pass glow for silky smooth bloom
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(30, 0, 230, 255)), 56), frontRect, screenRx, screenRx);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 230, 255)), 40), frontRect, screenRx, screenRx);
                dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 240, 255)), 30), frontRect, screenRx, screenRx);
            }

            double frontStroke = size <= 32 ? 34 : 26;
            dc.DrawRoundedRectangle(null, new Pen(frontGrad, frontStroke), frontRect, screenRx, screenRx);

            // 4. Central Lightning Bolt (Centered precisely inside front screen)
            double boltCenterX = frontRect.Left + (frontRect.Width / 2.0);
            double boltCenterY = frontRect.Top + (frontRect.Height / 2.0);

            // Scale factor for bolt (larger on small icons for razor sharpness)
            double boltScale = size <= 32 ? 1.25 : 1.05;

            var boltGeo = new StreamGeometry();
            using (var ctx = boltGeo.Open())
            {
                // Defined relative to center (0,0)
                Point p1 = new Point(boltCenterX + (18 * boltScale),  boltCenterY - (66 * boltScale)); // Top tip
                Point p2 = new Point(boltCenterX - (42 * boltScale),  boltCenterY + (12 * boltScale)); // Left mid
                Point p3 = new Point(boltCenterX - (4 * boltScale),   boltCenterY + (12 * boltScale)); // Center left waist
                Point p4 = new Point(boltCenterX - (20 * boltScale),  boltCenterY + (72 * boltScale)); // Bottom tip
                Point p5 = new Point(boltCenterX + (40 * boltScale),  boltCenterY - (6 * boltScale));  // Right mid
                Point p6 = new Point(boltCenterX + (2 * boltScale),   boltCenterY - (6 * boltScale));  // Center right waist

                ctx.BeginFigure(p1, isFilled: true, isClosed: true);
                ctx.LineTo(p2, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p3, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p4, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p5, isStroked: true, isSmoothJoin: false);
                ctx.LineTo(p6, isStroked: true, isSmoothJoin: false);
            }
            boltGeo.Freeze();

            // Glow for bolt on medium/large sizes
            if (size >= 64)
            {
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(100, 0, 220, 255)), 12), boltGeo);
            }
            dc.DrawGeometry(boltGrad, null, boltGeo);

            dc.Pop(); // Scale transform
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    static void BuildIcoFile((int size, byte[] data)[] images, string outputPath)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // ICONDIR header
        bw.Write((ushort)0); // idReserved
        bw.Write((ushort)1); // idType = 1 (icon)
        bw.Write((ushort)images.Length); // idCount

        int offset = 6 + (16 * images.Length);

        // Write ICONDIRENTRY for each image
        foreach (var img in images)
        {
            byte widthByte = img.size >= 256 ? (byte)0 : (byte)img.size;
            byte heightByte = img.size >= 256 ? (byte)0 : (byte)img.size;

            bw.Write(widthByte);          // bWidth
            bw.Write(heightByte);         // bHeight
            bw.Write((byte)0);            // bColorCount (0 for >= 8bpp)
            bw.Write((byte)0);            // bReserved
            bw.Write((ushort)1);          // wPlanes
            bw.Write((ushort)32);         // wBitCount
            bw.Write((uint)img.data.Length); // dwBytesInRes
            bw.Write((uint)offset);       // dwImageOffset

            offset += img.data.Length;
        }

        // Write PNG image data
        foreach (var img in images)
        {
            bw.Write(img.data);
        }
    }
}
