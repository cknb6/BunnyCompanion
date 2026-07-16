using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace BunnyCompanion.Services;

/// <summary>
/// 系统监控：CPU/内存/电池/闲置时长/进程。纯 .NET，无外部包。
/// 用于 Agent 工具查询与桌宠触发器（高负载提醒、低电量、久坐等）。
/// </summary>
public static class SystemMonitorService
{
    // ---------- 闲置时长（全局键盘鼠标无输入） ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    /// <summary>用户最后一次输入距今的秒数（键盘/鼠标无操作时长）。</summary>
    public static double GetIdleSeconds()
    {
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (GetLastInputInfo(ref info))
            {
                var tickCount = Environment.TickCount;
                // 跨越 49.7 天回滚的差值仍正确（uint 减法回绕）
                var idleMs = unchecked((uint)tickCount - info.Time);
                return idleMs / 1000.0;
            }
        }
        catch
        {
            // ignore
        }
        return 0;
    }

    // ---------- 电池 ----------

    public sealed record BatteryStatus(bool HasBattery, int LifePercent, bool OnAc, bool Charging, int BatteryLifeMinutes);

    public static BatteryStatus GetBattery()
    {
        try
        {
            var p = Forms.SystemInformation.PowerStatus;
            var chargeStatus = p.BatteryChargeStatus;
            var noBattery = chargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)
                            || !float.IsFinite(p.BatteryLifePercent)
                            || p.BatteryLifePercent < 0;
            if (noBattery)
                return new BatteryStatus(false, -1, true, false, -1);

            // WinForms 返回 0.0～1.0 的比例，不是 0～100；旧逻辑会把 67% 错报成 1%。
            var pct = (int)Math.Round(Math.Clamp(p.BatteryLifePercent, 0f, 1f) * 100);
            var onAc = p.PowerLineStatus == Forms.PowerLineStatus.Online;
            var charging = chargeStatus.HasFlag(Forms.BatteryChargeStatus.Charging);
            var minutes = p.BatteryLifeRemaining >= 0 ? p.BatteryLifeRemaining / 60 : -1;
            return new BatteryStatus(true, pct, onAc, charging, minutes);
        }
        catch
        {
            return new BatteryStatus(false, -1, true, false, -1);
        }
    }

    // ---------- 内存 ----------

    public sealed record MemoryStatus(long TotalPhysicalMb, long AvailableMb, long UsedMb, double UsagePercent, long ProcessWorkingSetMb);

    /// <summary>执行 PowerShell 取一个数值（字节/KB），失败返回 -1。</summary>
    private static long RunPsScalar(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-EncodedCommand");
            psi.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return -1;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(4000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { proc.WaitForExit(1000); } catch { /* ignore */ }
                return -1;
            }
            Task.WaitAll([stdoutTask, stderrTask], 1000);
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            return long.TryParse(stdout.Trim(), out var v) ? v : -1;
        }
        catch
        {
            return -1;
        }
    }

    public static MemoryStatus GetMemory()
    {
        try
        {
            // 工作集近似本进程占用；系统级用 GC + 进程列表估算
            var procMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;

            // 可用内存：用驱动器不可得，改用性能计数器；失败则用进程总量近似
            long totalMb = 0;
            long usedMb = 0;
            try
            {
                // Windows: 通过 PowerShell 取总/可用物理内存，避免引入 System.Diagnostics.PerformanceCounter 的实例名复杂性
                var total = RunPsScalar("(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory");
                var free = RunPsScalar("(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory");
                if (total > 0)
                {
                    totalMb = total / 1024 / 1024;
                    var freeMb = free > 0 ? free / 1024 : 0; // FreePhysicalMemory 单位是 KB
                    usedMb = totalMb - freeMb;
                }
            }
            catch
            {
                // ignore
            }

            if (totalMb <= 0)
            {
                // 系统内存读取失败时不要用当前进程冒充整机总量，否则会得到 100% 并误触发告警。
                return new MemoryStatus(0, 0, 0, 0, procMb);
            }

            var pct = totalMb > 0 ? usedMb * 100.0 / totalMb : 0;
            return new MemoryStatus(totalMb, totalMb - usedMb, usedMb, Math.Round(pct, 1), procMb);
        }
        catch
        {
            return new MemoryStatus(0, 0, 0, 0, 0);
        }
    }

    // ---------- CPU ----------

    /// <summary>采样约 0.5 秒估算整机 CPU 占用百分比（0~100）。</summary>
    public static double GetCpuUsagePercent()
    {
        try
        {
            // 优先用性能计数器 _Total 实例（仅 Windows，需实例存在）
            using var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            pc.NextValue(); // 第一次返回 0，需先取一次
            System.Threading.Thread.Sleep(500);
            var v = pc.NextValue();
            if (v >= 0 && v <= 100)
                return Math.Round(v, 1);
        }
        catch
        {
            // 性能计数器不可用（部分精简系统/无权限）→ 进程采样兜底
        }

        return EstimateCpuByProcessSampling();
    }

    /// <summary>用所有进程的 TotalProcessorTime 差值估算整机 CPU 占用（兜底，精度较低）。</summary>
    private static double EstimateCpuByProcessSampling()
    {
        try
        {
            var procs = Process.GetProcesses();
            TimeSpan SumCpu() => procs.Select(p =>
                {
                    try { return p.TotalProcessorTime; }
                    catch { return TimeSpan.Zero; }
                }).Aggregate(TimeSpan.Zero, (a, b) => a + b);

            var t1 = SumCpu();
            System.Threading.Thread.Sleep(400);
            var t2 = SumCpu();
            var delta = (t2 - t1).TotalMilliseconds;
            // 估算：进程 CPU 增量 / (采样时长 * 逻辑核数) * 100
            var cores = Math.Max(1, Environment.ProcessorCount);
            return Math.Round(Math.Clamp(delta / (400 * cores) * 100, 0, 100), 1);
        }
        catch
        {
            return 0;
        }
    }

    // ---------- 进程 ----------

    /// <summary>判断指定进程是否正在运行（按名称包含匹配）。</summary>
    public static bool IsProcessRunning(string namePart)
    {
        if (string.IsNullOrWhiteSpace(namePart))
            return false;
        try
        {
            return Process.GetProcesses().Any(p =>
            {
                try { return p.ProcessName.Contains(namePart, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>占用内存最高的 N 个进程（名称/PID/内存MB）。</summary>
    public static string TopProcessesByMemory(int top = 8)
    {
        try
        {
            var list = Process.GetProcesses()
                .Select(p =>
                {
                    try { return (p.ProcessName, p.Id, Mb: p.WorkingSet64 / 1024.0 / 1024.0); }
                    catch { return (ProcessName: "", Id: 0, Mb: 0.0); }
                })
                .Where(x => !string.IsNullOrEmpty(x.ProcessName))
                .OrderByDescending(x => x.Mb)
                .Take(Math.Clamp(top, 1, 30))
                .ToList();

            var sb = new StringBuilder();
            sb.AppendLine("进程\tPID\t内存MB");
            foreach (var p in list)
                sb.AppendLine($"{p.ProcessName}\t{p.Id}\t{p.Mb:F1}");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            return $"错误: {ex.Message}";
        }
    }

    // ---------- 综合快照 ----------

    /// <summary>一次性返回系统监控摘要，供 Agent 工具与触发器使用。</summary>
    public static string BuildSnapshot()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【系统监控】");

        var cpu = GetCpuUsagePercent();
        sb.AppendLine($"CPU 占用: {cpu:F1}%");

        var mem = GetMemory();
        if (mem.TotalPhysicalMb > 0)
            sb.AppendLine($"内存: 已用 {mem.UsedMb}MB / 共 {mem.TotalPhysicalMb}MB ({mem.UsagePercent}%) · 本进程 {mem.ProcessWorkingSetMb}MB");

        var bat = GetBattery();
        if (bat.HasBattery)
        {
            var src = bat.OnAc ? "外接电源" : "电池";
            sb.AppendLine($"电池: {bat.LifePercent}%（{src}{(bat.Charging ? "，充电中" : "")}）"
                          + (bat.BatteryLifeMinutes > 0 ? $" · 预计 {bat.BatteryLifeMinutes} 分钟" : ""));
        }
        else
        {
            sb.AppendLine("电池: 无（台式机或未检测到）");
        }

        var idle = GetIdleSeconds();
        sb.AppendLine($"闲置时长: {FormatIdle(idle)}");

        sb.AppendLine($"逻辑处理器: {Environment.ProcessorCount}");
        return sb.ToString().Trim();
    }

    private static string FormatIdle(double seconds)
    {
        if (seconds < 60) return $"{seconds:F0} 秒";
        if (seconds < 3600) return $"{seconds / 60:F1} 分钟";
        return $"{seconds / 3600:F1} 小时";
    }

    // ---------- 触发器规则 ----------

    /// <summary>触发器评估结果：是否触发、对应动作与气泡文案。</summary>
    public sealed record TriggerResult(string ActionKey, string Message, string Reason);

    /// <summary>
    /// 根据当前系统状态评估触发器。调用方（MainWindow）定时调用，节流后执行。
    /// 返回 null 表示本轮无触发。
    /// </summary>
    public static TriggerResult? EvaluateTriggers(SystemTriggerConfig cfg, double idleSeconds)
    {
        // 高 CPU 持续提醒（避免每次都触发，由调用方节流）
        try
        {
            var cpu = GetCpuUsagePercent();
            if (cfg.HighCpuThreshold > 0 && cpu >= cfg.HighCpuThreshold)
                return new TriggerResult("curious",
                    $"电脑有点忙哦，CPU {cpu:F0}% 了，要不要歇一下？",
                    $"cpu={cpu:F1}");

            var mem = GetMemory();
            if (cfg.HighMemoryThreshold > 0 && mem.UsagePercent >= cfg.HighMemoryThreshold)
                return new TriggerResult("comfort",
                    $"内存快满了（{mem.UsagePercent}%），关掉一些不用的窗口会顺一点～",
                    $"mem={mem.UsagePercent}");

            var bat = GetBattery();
            if (bat.HasBattery && !bat.OnAc && cfg.LowBatteryThreshold > 0
                && bat.LifePercent <= cfg.LowBatteryThreshold && bat.LifePercent >= 0)
                return new TriggerResult("reminder",
                    $"电量只剩 {bat.LifePercent}% 啦，记得接上电源～",
                    $"battery={bat.LifePercent}");

            // 久离欢迎仅由 MainWindow 在「返回操作」时触发（HasReturnedFromIdle）。
            // 人仍离开时不得在此弹出「回来啦」，故忽略 idleSeconds 参数。
        }
        catch
        {
            // 监控失败不阻断
        }
        return null;
    }
}
