using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

namespace BunnyCompanion.Services;

/// <summary>
/// 捕获当前主显示器或角色所在显示器的桌面截图，供多模态 Agent 理解屏幕内容。
/// </summary>
public static class DesktopCaptureService
{
    public sealed record CaptureResult(byte[] JpegBytes, string MimeType, int Width, int Height);

    public static CaptureResult? CaptureNearWindow(Window? window, int maxEdge = 1280)
    {
        try
        {
            var bounds = ResolveScreenBounds(window);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            }

            using var scaled = ScaleIfNeeded(bitmap, maxEdge);
            using var stream = new MemoryStream();
            var encoder = GetJpegEncoder();
            var quality = Encoder.Quality;
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(quality, 72L);
            if (encoder is not null)
                scaled.Save(stream, encoder, parameters);
            else
                scaled.Save(stream, ImageFormat.Jpeg);

            return new CaptureResult(stream.ToArray(), "image/jpeg", scaled.Width, scaled.Height);
        }
        catch
        {
            return null;
        }
    }

    private static Rectangle ResolveScreenBounds(Window? window)
    {
        try
        {
            if (window is { IsLoaded: true })
            {
                var width = window.ActualWidth > 0 ? window.ActualWidth : Math.Max(window.Width, 1);
                var height = window.ActualHeight > 0 ? window.ActualHeight : Math.Max(window.Height, 1);
                var center = window.PointToScreen(new System.Windows.Point(width / 2, height / 2));
                var screen = Forms.Screen.FromPoint(new System.Drawing.Point(
                    (int)Math.Round(center.X),
                    (int)Math.Round(center.Y)));
                return screen.Bounds;
            }
        }
        catch
        {
            // fall through
        }

        return Forms.Screen.PrimaryScreen?.Bounds
               ?? new Rectangle(0, 0, 1920, 1080);
    }

    private static Bitmap ScaleIfNeeded(Bitmap source, int maxEdge)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maxEdge)
            return (Bitmap)source.Clone();

        var scale = maxEdge / (double)longest;
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static ImageCodecInfo? GetJpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec =>
            codec.FormatID == ImageFormat.Jpeg.Guid);
}
