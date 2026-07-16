using BunnyCompanion.Services;

namespace BunnyCompanion.Models;

public sealed class PetSettings
{
    public string PetName { get; set; } = "小申";
    public string PartnerName { get; set; } = "宝宝";
    public double Scale { get; set; } = 1.0;
    public bool AlwaysOnTop { get; set; } = true;
    public bool AutoWalk { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    /// <summary>是否用语音朗读 Agent 回复（TTS，需 Windows SAPI）。</summary>
    public bool TtsEnabled { get; set; }
    /// <summary>是否启用语音输入按钮（语音识别，需 Windows SAPI）。</summary>
    public bool VoiceInputEnabled { get; set; } = true;
    public bool ShowSpeechBubbles { get; set; } = true;
    public bool QuietMode { get; set; }
    public bool HideForFullscreen { get; set; } = true;
    public bool ClickThrough { get; set; }
    public int WaterReminderMinutes { get; set; } = 60;
    public int RestReminderMinutes { get; set; } = 50;
    public string QuietStart { get; set; } = "23:00";
    public string QuietEnd { get; set; } = "08:00";
    public DateTime? Birthday { get; set; }
    public DateTime? Anniversary { get; set; }
    public int Affection { get; set; }
    public int InteractionCount { get; set; }
    public DateTime FirstMetDate { get; set; } = DateTime.Today;
    public double? LastLeft { get; set; }
    public double? LastTop { get; set; }
    public bool HasCompletedFirstRun { get; set; }
    public List<string> LoveMessages { get; set; } = DefaultMessages();
    /// <summary>系统监控触发器配置（CPU/内存/电池/久坐提醒阈值）。</summary>
    public SystemTriggerConfig SystemTriggers { get; set; } = new();

    public static List<string> DefaultMessages() =>
    [
        "{name}，今天也要好好照顾自己呀",
        "我会安安静静陪着你",
        "累了就摸摸我的头吧",
        "记得喝水，也记得想我一下",
        "你认真做事的样子特别可爱",
        "今天也比昨天更喜欢你一点",
        "不管多忙，都要记得好好吃饭",
        "送你一个只属于今天的抱抱",
        "看到我，就代表有人正在想你",
        "把烦恼分我一半，好不好",
        "辛苦啦，休息一会儿再继续",
        "今天的你也闪闪发光",
    ];

    public void Normalize()
    {
        PetName = string.IsNullOrWhiteSpace(PetName) ? "小申" : PetName.Trim();
        if (PetName.Length > 20)
            PetName = PetName[..20];
        PartnerName = string.IsNullOrWhiteSpace(PartnerName) ? "宝宝" : PartnerName.Trim();
        // 历史默认「宝贝」迁移为「宝宝」
        if (PartnerName == "宝贝")
            PartnerName = "宝宝";
        if (PartnerName.Length > 20)
            PartnerName = PartnerName[..20];
        Scale = Math.Clamp(Scale, 0.65, 1.50);
        WaterReminderMinutes = Math.Clamp(WaterReminderMinutes, 0, 240);
        RestReminderMinutes = Math.Clamp(RestReminderMinutes, 0, 240);
        QuietStart = NormalizeTimeText(QuietStart, "23:00");
        QuietEnd = NormalizeTimeText(QuietEnd, "08:00");
        LoveMessages = LoveMessages?.Where(message => !string.IsNullOrWhiteSpace(message))
            .Select(message => message.Trim()).Take(80).ToList() ?? [];
        if (LoveMessages.Count == 0)
            LoveMessages = DefaultMessages();
        if (FirstMetDate == default)
            FirstMetDate = DateTime.Today;
    }

    private static string NormalizeTimeText(string? value, string fallback) =>
        TimeOnly.TryParse(value, out var time) ? time.ToString("HH:mm") : fallback;
}
