using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Point = System.Windows.Point;

namespace BunnyCompanion.Services;

public static class ScreenService
{
    /// <summary>
    /// 返回窗口中心所在显示器的工作区（WPF 设备无关单位），兼容多显示器不同缩放。
    /// </summary>
    public static Rect GetWorkingArea(Window window)
    {
        try
        {
            if (PresentationSource.FromVisual(window) is not null)
            {
                var width = window.ActualWidth > 0 ? window.ActualWidth : Math.Max(window.Width, 1);
                var height = window.ActualHeight > 0 ? window.ActualHeight : Math.Max(window.Height, 1);
                var centerPixels = window.PointToScreen(new Point(width / 2, height / 2));
                var center = new Drawing.Point(
                    (int)Math.Round(centerPixels.X),
                    (int)Math.Round(centerPixels.Y));
                var area = Forms.Screen.FromPoint(center).WorkingArea;

                // 用 PointFromScreen 做 per-monitor DPI 换算，避免不同缩放屏位置跑偏。
                var topLeft = window.PointFromScreen(new Point(area.Left, area.Top));
                var bottomRight = window.PointFromScreen(new Point(area.Right, area.Bottom));
                return new Rect(
                    window.Left + topLeft.X,
                    window.Top + topLeft.Y,
                    Math.Max(1, bottomRight.X - topLeft.X),
                    Math.Max(1, bottomRight.Y - topLeft.Y));
            }
        }
        catch (InvalidOperationException)
        {
            // 窗口句柄尚未就绪
        }
        catch
        {
            // 回退到主屏估算
        }

        return GetPrimaryWorkingAreaFallback(window);
    }

    public static void ClampToWorkingArea(Window window)
    {
        var area = GetWorkingArea(window);
        var maxLeft = Math.Max(area.Left, area.Right - Math.Max(window.Width, 1));
        var maxTop = Math.Max(area.Top, area.Bottom - Math.Max(window.Height, 1));
        window.Left = Math.Clamp(window.Left, area.Left, maxLeft);
        window.Top = Math.Clamp(window.Top, area.Top, maxTop);
    }

    public static void PlaceBottomRight(Window window, double marginX = 24, double marginY = 6)
    {
        var area = GetWorkingArea(window);
        window.Left = area.Right - window.Width - marginX;
        window.Top = area.Bottom - window.Height - marginY;
        ClampToWorkingArea(window);
    }

    private static Rect GetPrimaryWorkingAreaFallback(Window window)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? Forms.SystemInformation.VirtualScreen;
        return new Rect(
            area.Left / scaleX,
            area.Top / scaleY,
            area.Width / scaleX,
            area.Height / scaleY);
    }
}
