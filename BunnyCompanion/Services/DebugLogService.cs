using System.IO;
using System.Text;

namespace BunnyCompanion.Services;

/// <summary>
/// 调试日志：常开、线程安全、按大小滚动，写入 %LocalAppData%\BunnyCompanion\Logs\debug.log。
/// 记录工具调用、搜索引擎尝试、更新检查等关键链路，方便用户直接把日志发给开发者排查。
/// 注意：日志中不得写入 API Key、模型名等接口细节。
/// </summary>
public static class DebugLogService
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024; // 超过 2MB 滚动为 debug.old.log

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BunnyCompanion", "Logs");

    public static string LogPath => Path.Combine(LogDirectory, "debug.log");

    /// <summary>写一条日志。category 例如 update / search / tool / startup。永不抛异常。</summary>
    public static void Log(string category, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}\n";
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    /// <summary>记录异常（含内层异常，截断避免刷爆）。</summary>
    public static void LogError(string category, string context, Exception ex)
    {
        var msg = $"{context} | {ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is not null)
            msg += $" | inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
        Log(category, msg.Length > 800 ? msg[..800] : msg);
    }

    private static void RotateIfNeeded()
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (!fi.Exists || fi.Length < MaxBytes)
                return;
            var old = Path.Combine(LogDirectory, "debug.old.log");
            if (File.Exists(old))
                File.Delete(old);
            File.Move(LogPath, old);
        }
        catch
        {
            // ignore
        }
    }
}
