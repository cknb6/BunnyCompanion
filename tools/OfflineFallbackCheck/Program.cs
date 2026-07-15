using BunnyCompanion.Models;
using BunnyCompanion.Services;

// 驱动已交付的 ChatReplyService.Reply（真实源文件链接编译），禁止复刻逻辑。
var settings = new PetSettings { PartnerName = "宝贝", PetName = "小申" };
var samples = new[]
{
    "",
    "你好",
    "好累呀",
    "帮我写代码",
    "看桌面",
    "asdfqwer1234!@#",
    "想你了",
    "晚安",
};

Console.WriteLine($"RuleGroupCount={ChatReplyService.RuleGroupCount}");
Console.WriteLine($"FallbackCount={ChatReplyService.FallbackCount}");

if (ChatReplyService.RuleGroupCount < 30)
    throw new Exception($"规则组不足 30：{ChatReplyService.RuleGroupCount}");
if (ChatReplyService.FallbackCount < 40)
    throw new Exception($"通用兜底不足 40：{ChatReplyService.FallbackCount}");

foreach (var sample in samples)
{
    var reply = ChatReplyService.Reply(sample, settings, offlineMode: true);
    if (string.IsNullOrWhiteSpace(reply.Text))
        throw new Exception($"空回复：sample=[{sample}]");
    Console.WriteLine($"SAMPLE\t{sample}\t=>\t{reply.Text}\t[{reply.ActionKey}]");
}

// 多样性：清空情话池，强制走通用 FallbackReplies（真实 Reply 入口，不复刻逻辑）
var diversitySettings = new PetSettings
{
    PartnerName = "宝贝",
    PetName = "小申",
    LoveMessages = [],
};
var distinct = new HashSet<string>(StringComparer.Ordinal);
for (var i = 0; i < 60; i++)
{
    var reply = ChatReplyService.Reply($"无匹配输入_{i}_※※※", diversitySettings, offlineMode: true);
    if (string.IsNullOrWhiteSpace(reply.Text))
        throw new Exception("无关键词命中出现空回复");
    distinct.Add(reply.Text);
    if (i < 12) Console.WriteLine($"DIV{i}	{reply.Text}");
}
Console.WriteLine($"DISTINCT_FALLBACKS={distinct.Count}");
if (distinct.Count < 8)
    throw new Exception($"兜底多样性不足：仅 {distinct.Count} 条不同文案");

Console.WriteLine("OFFLINE_FALLBACK_OK");
