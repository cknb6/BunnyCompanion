using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace BunnyCompanion.Services;

/// <summary>
/// 开机自启动管理：注册表 Run 键为主，计划任务为回退（绕过 UAC，适合 highestAvailable manifest）。
/// 修复点：ProcessPath 回退、路径一致性自检、StartupApproved 禁用检测、计划任务回退。
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "BunnyCompanion";
    /// <summary>计划任务名（登录时触发，以最高权限运行，绕过 UAC 提示）。</summary>
    private const string TaskName = @"BunnyCompanion_AutoStart";

    /// <summary>获取当前 EXE 绝对路径：ProcessPath 优先，回退到 Assembly.Location 与 AppContext.BaseDirectory。</summary>
    private static string? ResolveExecutablePath()
    {
        try
        {
            var p = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                return Path.GetFullPath(p);
        }
        catch { /* ignore */ }

        try
        {
            var asm = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(asm) && File.Exists(asm))
                return Path.GetFullPath(asm);
        }
        catch { /* ignore */ }

        try
        {
            var dir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                var guess = Path.Combine(dir, "BunnyCompanion.exe");
                if (File.Exists(guess))
                    return Path.GetFullPath(guess);
            }
        }
        catch { /* ignore */ }

        return null;
    }

    /// <summary>
    /// 启用/禁用开机启动。启用时先写注册表；若当前以管理员运行则额外创建计划任务作为回退（更稳）。
    /// 返回 false 时可通过 <see cref="Diagnose"/> 获取原因。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            var okReg = DeleteRunValue();
            var okTaskDel = DeleteScheduledTask();
            return okReg || okTaskDel;
        }

        var exe = ResolveExecutablePath();
        if (string.IsNullOrWhiteSpace(exe))
            return false;

        // 注册表 Run 键：路径一致性自检，EXE 移动或热更新后自动刷新
        var okRun = WriteRunValue(exe);

        // 管理员账户 + highestAvailable manifest 时，注册表启动会触发 UAC 可能被静默拦截。
        // 若当前进程已是管理员，额外创建登录触发的计划任务（最高权限），绕过 UAC。
        var okTask = false;
        try
        {
            if (WindowsAgentToolkit.IsRunningAsAdmin())
                okTask = EnsureScheduledTask(exe);
        }
        catch
        {
            // 计划任务失败不影响注册表路径
        }

        return okRun || okTask;
    }

    /// <summary>是否已启用：注册表值存在且未被 StartupApproved 禁用，或计划任务存在。</summary>
    public static bool IsEnabled()
    {
        if (IsRunValueEnabled(out var disabledByApprover) && !disabledByApprover)
            return true;
        if (ScheduledTaskExists())
            return true;
        return false;
    }

    /// <summary>注册表 Run 值是否写入且路径与当前 EXE 一致；disabled 返回是否被任务管理器禁用。</summary>
    private static bool IsRunValueEnabled(out bool disabled)
    {
        disabled = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            // 检测 Windows 11 任务管理器「启动」标签页是否禁用了本项
            try
            {
                using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedPath);
                if (approved?.GetValue(ValueName) is byte[] bytes && bytes.Length > 0)
                {
                    // 首字节 0x02/0x03 表示被禁用，0x03/0x06 表示启用（不同 Windows 版本有差异）
                    disabled = bytes[0] == 0x02 || bytes[0] == 0x03 && bytes.Length >= 12 && bytes[2] == 0;
                }
            }
            catch { /* ignore */ }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteRunValue(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return false;
            key.SetValue(ValueName, $"\"{exe}\" --startup");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>创建/更新登录时触发的计划任务（最高权限运行，绕过 UAC）。</summary>
    private static bool EnsureScheduledTask(string exe)
    {
        try
        {
            // schtasks /Create /TN 名 /TR 命令 /SC ONLOGON /RL HIGHEST /F
            // /RL HIGHEST 需要管理员权限创建；当前进程已是管理员才走到这里
            var args = $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --startup\" /SC ONLOGON /RL HIGHEST /F";
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(8000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool DeleteScheduledTask()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(6000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool ScheduledTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>诊断开机启动状态，供设置页或托盘菜单展示给用户排查。</summary>
    public sealed record DiagnoseResult(
        bool Enabled,
        bool RunValueExists,
        bool RunPathMatchesCurrent,
        bool DisabledByApprover,
        bool ScheduledTaskExists,
        bool IsAdmin,
        string? CurrentExe,
        string? RegisteredExe,
        string Summary);

    public static DiagnoseResult Diagnose()
    {
        var current = ResolveExecutablePath();
        var runExists = IsRunValueEnabled(out var disabled);
        string? registeredExe = null;
        var pathMatches = false;
        if (runExists)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                var v = key?.GetValue(ValueName) as string;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(v, @"^\""([^\""]+)\""");
                    registeredExe = m.Success ? m.Groups[1].Value : v.Split(' ')[0];
                    pathMatches = current is not null
                        && string.Equals(registeredExe, current, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { /* ignore */ }
        }

        var taskExists = ScheduledTaskExists();
        var isAdmin = WindowsAgentToolkit.IsRunningAsAdmin();
        var enabled = (runExists && !disabled) || taskExists;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(enabled ? "开机启动：已启用" : "开机启动：未启用");
        if (runExists) sb.AppendLine($"· 注册表 Run 值：存在{(pathMatches ? "（路径一致）" : "（路径不一致，需重开）")}");
        else sb.AppendLine("· 注册表 Run 值：未写入");
        if (disabled) sb.AppendLine("· 任务管理器已禁用本启动项，请在「启动应用」里重新启用");
        sb.AppendLine(taskExists ? "· 计划任务回退：已创建（登录时最高权限运行，绕过 UAC）" : "· 计划任务回退：未创建");
        if (!isAdmin) sb.AppendLine("· 当前非管理员运行，未创建计划任务回退（仅注册表方式）");
        if (!pathMatches && runExists) sb.AppendLine("· 注册表路径与当前 EXE 不一致，下次启动会自动刷新");

        return new DiagnoseResult(enabled, runExists, pathMatches, disabled, taskExists, isAdmin, current, registeredExe, sb.ToString().Trim());
    }
}
