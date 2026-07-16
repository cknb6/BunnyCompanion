namespace BunnyCompanion.Services;

/// <summary>系统触发器阈值配置（持久化在 settings.json）。无平台依赖，可被测试项目链接。</summary>
public sealed class SystemTriggerConfig
{
    /// <summary>CPU 占用百分比阈值，超过则提醒。0 表示关闭。</summary>
    public double HighCpuThreshold { get; set; } = 85;
    /// <summary>内存占用百分比阈值。0 表示关闭。</summary>
    public double HighMemoryThreshold { get; set; } = 90;
    /// <summary>低电量百分比阈值。0 表示关闭。</summary>
    public int LowBatteryThreshold { get; set; } = 20;
    /// <summary>闲置过久秒数（离开电脑）。0 表示关闭。</summary>
    public double IdleTooLongSeconds { get; set; } = 600;
    /// <summary>触发器总开关。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>两次同类触发最小间隔秒数（节流，避免刷屏）。</summary>
    public double CooldownSeconds { get; set; } = 600;

    /// <summary>清理损坏或手工编辑产生的越界配置，避免定时器进入异常状态。</summary>
    public void Normalize()
    {
        HighCpuThreshold = NormalizePercent(HighCpuThreshold, 85);
        HighMemoryThreshold = NormalizePercent(HighMemoryThreshold, 90);
        LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 0, 100);
        IdleTooLongSeconds = NormalizeSeconds(IdleTooLongSeconds, 600, allowZero: true);
        CooldownSeconds = NormalizeSeconds(CooldownSeconds, 600, allowZero: false);
    }

    /// <summary>
    /// 判断用户是否刚从长时间离开状态返回。提醒应在返回后出现，而不是在人不在时消失。
    /// </summary>
    public static bool HasReturnedFromIdle(bool wasAway, double idleSeconds, double awayThresholdSeconds)
    {
        if (!wasAway || awayThresholdSeconds <= 0 || !double.IsFinite(idleSeconds))
            return false;
        // 当前闲置值必须已回到阈值内，避免用户短暂输入后又离开时误弹欢迎。
        return idleSeconds >= 0 && idleSeconds < awayThresholdSeconds;
    }

    private static double NormalizePercent(double value, double fallback)
    {
        if (!double.IsFinite(value))
            return fallback;
        return Math.Clamp(value, 0, 100);
    }

    private static double NormalizeSeconds(double value, double fallback, bool allowZero)
    {
        if (!double.IsFinite(value))
            return fallback;
        if (allowZero && value <= 0)
            return 0;
        return Math.Clamp(value, 60, 24 * 60 * 60);
    }
}
