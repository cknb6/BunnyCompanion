using System.Diagnostics;
using System.IO;
using System.Text;

namespace BunnyCompanion.Services;

/// <summary>
/// 一键卸载：清理开机启动、本地配置/日志，并在进程退出后尝试删除 EXE 自身。
/// </summary>
public static class UninstallService
{
    public sealed record Result(
        bool StartupCleared,
        bool DataDeleted,
        bool DeleteScheduled,
        string ConfigDirectory,
        string? ExecutablePath,
        string Summary);

    /// <summary>
    /// 执行卸载清理（调用方应在此之后不再 SaveSettings，并立即退出）。
    /// </summary>
    public static Result Run(string configDirectory)
    {
        var startupOk = StartupService.SetEnabled(false);
        // 再删一次，避免键名残留
        try { StartupService.SetEnabled(false); } catch { /* ignore */ }

        var dataOk = true;
        try
        {
            if (Directory.Exists(configDirectory))
            {
                // 先尽量去掉只读，再整目录删除
                foreach (var path in Directory.EnumerateFileSystemEntries(configDirectory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var attr = File.GetAttributes(path);
                        if ((attr & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
                    }
                    catch { /* ignore */ }
                }

                Directory.Delete(configDirectory, recursive: true);
            }
        }
        catch
        {
            dataOk = false;
            try
            {
                // 二次尽力：删文件
                if (Directory.Exists(configDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(configDirectory, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(file); } catch { /* ignore */ }
                    }
                }
            }
            catch { /* ignore */ }
        }

        // 若目录仍在且为空，再试一次
        try
        {
            if (Directory.Exists(configDirectory) &&
                !Directory.EnumerateFileSystemEntries(configDirectory).Any())
            {
                Directory.Delete(configDirectory, false);
            }
        }
        catch { /* ignore */ }

        var stillThere = Directory.Exists(configDirectory);
        dataOk = dataOk && !stillThere;

        var exe = Environment.ProcessPath;
        var deleteScheduled = false;
        if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            deleteScheduled = ScheduleSelfDelete(exe);

        var sb = new StringBuilder();
        sb.AppendLine(startupOk ? "· 开机启动项已移除" : "· 开机启动项可能未完全移除（可稍后在「启动应用」中手动关闭）");
        sb.AppendLine(dataOk
            ? "· 本地数据已删除（设置 / 爱心值 / 日志）"
            : $"· 本地数据可能未删净，请手动删除文件夹：\n  {configDirectory}");
        sb.AppendLine(deleteScheduled
            ? "· 已安排删除程序文件，退出后几秒内自动清理 EXE"
            : "· 未能自动删除 EXE，请退出后手动删除程序文件");
        sb.AppendLine();
        sb.Append("小申会退出。卸载流程已执行完毕。");

        return new Result(startupOk, dataOk, deleteScheduled, configDirectory, exe, sb.ToString());
    }

    /// <summary>
    /// 通过 cmd 延迟删除当前 EXE（Windows 运行中无法直接删自身）。
    /// </summary>
    private static bool ScheduleSelfDelete(string executablePath)
    {
        try
        {
            // 转义引号，delay 后 del /f /q，并尝试删同目录下常见附属名
            var path = executablePath.Replace("\"", "");
            var dir = Path.GetDirectoryName(path) ?? "";
            // ping 作延迟；>nul 隐藏输出
            var cmd =
                $"/c ping 127.0.0.1 -n 3 >nul & " +
                $"del /f /q \"{path}\" >nul 2>&1 & " +
                $"if exist \"{path}\" del /f /q \"{path}\" >nul 2>&1";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmd,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = string.IsNullOrEmpty(dir) ? Environment.SystemDirectory : dir,
            };
            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
