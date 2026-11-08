using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace ImGui4NET.Sample.OpenGL3;

/// <summary>
/// Captures the OpenGL back buffer to a PNG file so the rendered UI can be
/// inspected without a live window. Press F12, or pass a path on the command
/// line to grab a frame automatically.
/// </summary>
internal static class Screenshot
{
    /// <summary>Reads the current back buffer and writes it as a PNG.</summary>
    public static void Capture(int width, int height, string path)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var pixels = new byte[width * height * 4];
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, width, height, GlPixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

        try
        {
            // glReadPixels returns bottom-up rows while Bitmap is top-down.
            int stride = data.Stride;
            var row = new byte[width * 4];
            for (int y = 0; y < height; y++)
            {
                int src = (height - 1 - y) * width * 4;
                System.Buffer.BlockCopy(pixels, src, row, 0, width * 4);

                // RGBA -> BGRA
                for (int x = 0; x < width * 4; x += 4)
                {
                    (row[x], row[x + 2]) = (row[x + 2], row[x]);
                }

                Marshal.Copy(row, 0, data.Scan0 + y * stride, width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"[screenshot] {Path.GetFullPath(path)} ({width}x{height})");
    }
}
