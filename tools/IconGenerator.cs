using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class IconGenerator
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            return 1;
        }

        using (Bitmap bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            RectangleF tile = new RectangleF(16, 16, 224, 224);
            using (GraphicsPath tilePath = RoundedRectangle(tile, 54))
            using (LinearGradientBrush gradient = new LinearGradientBrush(tile, Color.FromArgb(255, 130, 104, 255), Color.FromArgb(255, 37, 217, 199), 45f))
            {
                graphics.FillPath(gradient, tilePath);
            }

            using (SolidBrush glow = new SolidBrush(Color.FromArgb(42, 255, 255, 255)))
            {
                graphics.FillEllipse(glow, 52, 44, 152, 152);
            }
            using (Pen ring = new Pen(Color.White, 17f))
            {
                ring.StartCap = LineCap.Round;
                ring.EndCap = LineCap.Round;
                graphics.DrawArc(ring, 67, 67, 122, 122, -82, 286);
            }
            using (SolidBrush dot = new SolidBrush(Color.White))
            {
                graphics.FillEllipse(dot, 112, 112, 32, 32);
            }

            using (MemoryStream png = new MemoryStream())
            {
                bitmap.Save(png, ImageFormat.Png);
                byte[] image = png.ToArray();
                using (FileStream output = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
                using (BinaryWriter writer = new BinaryWriter(output))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)1);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)image.Length);
                    writer.Write((uint)22);
                    writer.Write(image);
                }
            }
        }
        return 0;
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
