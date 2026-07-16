using System.Text;

namespace BunnyCompanion.Services;

/// <summary>
/// 日常趣味陪伴：今日运势摘要、穿搭提示、小习惯建议等（本地算法）。
/// </summary>
public static class DailyCompanion
{
    public static string BuildDailyCard(string partnerName, DateOnly? day = null)
    {
        day ??= DateOnly.FromDateTime(DateTime.Today);
        var rng = new Random(Hash($"{day:yyyyMMdd}|{partnerName}"));
        var energy = 50 + rng.Next(0, 46);
        var focus = 50 + rng.Next(0, 46);
        var social = 45 + rng.Next(0, 51);
        var outfit = new[]
        {
            "舒服的浅色上衣 + 干净球鞋，像被阳光轻轻抱住",
            "一件软软外套，温度合适时搭白T，干练又乖",
            "家里办公就选不勒腰的居家装，开心比精致重要",
            "如果出门见人，选你穿过会收到夸奖的那套",
            "深色下装 + 亮色点缀（小配饰就好），精神又省事",
        }[rng.Next(5)];
        var meal = new[]
        {
            "热乎一餐比外卖更治愈，记得加蔬菜",
            "忙的话也要正经吃口碳水，别只靠咖啡",
            "想吃甜的可以，但先喝口水再决定",
            "晚餐别太撑，留给睡眠一点空间",
        }[rng.Next(4)];
        var habit = new[]
        {
            "每工作 50 分钟，起来走 2 分钟",
            "把手机拿远一点，眼睛会谢谢你",
            "今天给自己点一个小奖励任务",
            "睡前写三件今天还行的小事",
            "跟重要的人报个平安，不用长篇",
        }[rng.Next(5)];
        var quote = new[]
        {
            "慢慢来，也比较快。",
            "你已经比昨天更会照顾自己了。",
            "被喜欢着的人，值得好好吃饭。",
            "小步前进，也是前进。",
            "世界很大，但桌角有我。",
        }[rng.Next(5)];

        var sb = new StringBuilder();
        sb.AppendLine($"【今日陪伴卡 · {day:M月d日}】");
        sb.AppendLine($"给 {partnerName}");
        sb.AppendLine($"能量 {energy} · 专注 {focus} · 社交 {social}");
        sb.AppendLine($"穿搭小提示: {outfit}");
        sb.AppendLine($"吃饭: {meal}");
        sb.AppendLine($"小习惯: {habit}");
        sb.AppendLine($"一句话: {quote}");
        return sb.ToString().Trim();
    }

    public static string BuildMoodReply(string? moodHint, string partnerName)
    {
        var m = (moodHint ?? "").Trim();
        if (m.Contains("累") || m.Contains("疲惫"))
            return $"{partnerName}，累了就靠一下。先喝口水，允许自己摆烂十五分钟。";
        if (m.Contains("开心") || m.Contains("高兴"))
            return $"看到你开心，我耳朵都竖起来了～把开心存一点给晚上的自己。";
        if (m.Contains("焦虑") || m.Contains("慌") || m.Contains("紧张"))
            return "焦虑来的时候，数四次呼吸。事情可以拆小，我陪你一块一块做。";
        if (m.Contains("难过") || m.Contains("伤心") || m.Contains("emo"))
            return "难过可以待一会儿，不用立刻好起来。我在，不催你笑。";
        return $"{partnerName}，今天的情绪我接住了。想展开说也可以，不想说就摸摸我。";
    }

    private static int Hash(string s)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in s) h = h * 31 + c;
            return h & 0x7fffffff;
        }
    }
}
