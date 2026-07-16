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
}
