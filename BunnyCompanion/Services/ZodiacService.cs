using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 星座分析（趣味陪伴向）：按生日或星座名生成性格/今日运势等，纯本地规则。
/// </summary>
public static class ZodiacService
{
    public sealed record ZodiacProfile(
        string NameZh,
        string NameEn,
        string DateRange,
        string Element,
        string[] Traits,
        string[] Strengths,
        string[] Watchouts);

    private static readonly ZodiacProfile[] Profiles =
    [
        P("白羊座", "Aries", "3/21-4/19", "火", ["热情", "行动派", "直接"], ["冲劲足", "带头力"], ["别太急躁"]),
        P("金牛座", "Taurus", "4/20-5/20", "土", ["稳重", "踏实", "享受"], ["靠谱", "审美"], ["别太固执"]),
        P("双子座", "Gemini", "5/21-6/21", "风", ["机灵", "好奇", "善聊"], ["适应快", "点子多"], ["别三心二意"]),
        P("巨蟹座", "Cancer", "6/22-7/22", "水", ["细腻", "顾家", "共情"], ["温柔", "记忆力"], ["别过度操心"]),
        P("狮子座", "Leo", "7/23-8/22", "火", ["自信", "大方", "光芒"], ["领导力", "感染力"], ["别太要面子"]),
        P("处女座", "Virgo", "8/23-9/22", "土", ["细致", "条理", "追求完美"], ["靠谱", "分析力"], ["别过度自我苛责"]),
        P("天秤座", "Libra", "9/23-10/23", "风", ["优雅", "公平", "合群"], ["审美", "协调"], ["别犹豫太久"]),
        P("天蝎座", "Scorpio", "10/24-11/22", "水", ["深刻", "专注", "洞察"], ["意志力", "忠诚"], ["别把一切藏太深"]),
        P("射手座", "Sagittarius", "11/23-12/21", "火", ["乐观", "自由", "爱探索"], ["开朗", "坦诚"], ["别逃避责任"]),
        P("摩羯座", "Capricorn", "12/22-1/19", "土", ["务实", "坚韧", "目标感"], ["执行力", "担当"], ["别给自己加压过度"]),
        P("水瓶座", "Aquarius", "1/20-2/18", "风", ["独立", "创意", "前瞻"], ["独特", "友善"], ["别太疏离"]),
        P("双鱼座", "Pisces", "2/19-3/20", "水", ["浪漫", "直觉", "温柔"], ["想象力", "共情"], ["别太容易心软耗尽"]),
    ];

    private static ZodiacProfile P(string zh, string en, string range, string el, string[] t, string[] s, string[] w) =>
        new(zh, en, range, el, t, s, w);

    public static ZodiacProfile? FromDate(DateOnly date)
    {
        var m = date.Month;
        var d = date.Day;
        return (m, d) switch
        {
            (3, >= 21) or (4, <= 19) => Profiles[0],
            (4, >= 20) or (5, <= 20) => Profiles[1],
            (5, >= 21) or (6, <= 21) => Profiles[2],
            (6, >= 22) or (7, <= 22) => Profiles[3],
            (7, >= 23) or (8, <= 22) => Profiles[4],
            (8, >= 23) or (9, <= 22) => Profiles[5],
            (9, >= 23) or (10, <= 23) => Profiles[6],
            (10, >= 24) or (11, <= 22) => Profiles[7],
            (11, >= 23) or (12, <= 21) => Profiles[8],
            (12, >= 22) or (1, <= 19) => Profiles[9],
            (1, >= 20) or (2, <= 18) => Profiles[10],
            (2, >= 19) or (3, <= 20) => Profiles[11],
            _ => null,
        };
    }

    public static ZodiacProfile? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name.Trim().Replace("座", "", StringComparison.Ordinal) + "座";
        return Profiles.FirstOrDefault(p =>
            p.NameZh.Equals(n, StringComparison.OrdinalIgnoreCase)
            || p.NameZh.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase)
            || p.NameEn.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        string[] formats = ["yyyy-M-d", "yyyy/M/d", "yyyy.M.d", "M-d", "M/d", "M.d", "yyyy年M月d日", "M月d日"];
        if (DateOnly.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        var m = Regex.Match(text, @"(\d{4})[年/\-.](\d{1,2})[月/\-.](\d{1,2})");
        if (m.Success)
            return DateOnly.TryParse($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", out date);
        m = Regex.Match(text, @"(\d{1,2})[月/\-.](\d{1,2})");
        if (m.Success)
        {
            var y = DateTime.Today.Year;
            return DateOnly.TryParse($"{y}-{m.Groups[1].Value}-{m.Groups[2].Value}", out date);
        }
        return DateOnly.TryParse(text, out date);
    }

    /// <summary>完整趣味分析文案。</summary>
    public static string Analyze(string? dateOrName, string partnerName = "宝宝", DateOnly? today = null)
    {
        today ??= DateOnly.FromDateTime(DateTime.Today);
        ZodiacProfile? profile = null;
        DateOnly? birth = null;
        if (TryParseDate(dateOrName, out var d))
        {
            birth = d;
            profile = FromDate(d);
        }
        else
            profile = FromName(dateOrName);

        if (profile is null)
            return "没识别出星座～可以发生日如「1999-08-15」或「处女座」。";

        var seed = Hash($"{profile.NameZh}|{today:yyyy-MM-dd}|{partnerName}");
        var rng = new Random(seed);
        var luck = 55 + rng.Next(0, 41); // 55-95
        var love = 50 + rng.Next(0, 46);
        var career = 50 + rng.Next(0, 46);
        var mood = new[] { "偏暖", "轻快", "细腻", "坚定", "松弛", "微甜" }[rng.Next(6)];
        var color = new[] { "奶茶色", "雾霾蓝", "奶油白", "樱花粉", "薄荷绿", "香槟金" }[rng.Next(6)];
        var number = rng.Next(1, 10);
        var tip = new[]
        {
            "先做最重要的一件小事，完成感会推你前进。",
            "对在意的人说一句真心话，比发十句表情包有用。",
            "喝水、伸懒腰、看远处——身体先被照顾，心才跟得上。",
            "今天适合整理桌面或思路，清爽会带来好运感。",
            "允许自己慢半拍，节奏对了才不累。",
            "把喜欢写进备忘录，晚上回顾会很治愈。",
        }[rng.Next(6)];

        var sb = new StringBuilder();
        sb.AppendLine("【星座分析 · 趣味陪伴】");
        sb.AppendLine($"星座: {profile.NameZh}（{profile.NameEn}）· {profile.DateRange} · {profile.Element}象");
        if (birth is { } b)
            sb.AppendLine($"生日参考: {b:M月d日}");
        sb.AppendLine($"关键词: {string.Join(" · ", profile.Traits)}");
        sb.AppendLine($"闪光点: {string.Join("、", profile.Strengths)}");
        sb.AppendLine($"轻提醒: {string.Join("、", profile.Watchouts)}");
        sb.AppendLine();
        sb.AppendLine($"【今日运势 · {today:M月d日}】");
        sb.AppendLine($"综合 {luck} · 感情 {love} · 事业/学业 {career} · 心情 {mood}");
        sb.AppendLine($"幸运色 {color} · 幸运数 {number}");
        sb.AppendLine($"给{partnerName}的一句: {tip}");
        sb.AppendLine();
        sb.AppendLine("说明: 纯娱乐向桌宠解读，不当作严肃决策依据。");
        return sb.ToString().Trim();
    }

    public static string ListAll() =>
        string.Join("\n", Profiles.Select(p => $"{p.NameZh} {p.DateRange} · {p.Element} · {string.Join("/", p.Traits.Take(2))}"));

    private static int Hash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var c in s)
                h = h * 31 + c;
            return h & 0x7fffffff;
        }
    }
}
