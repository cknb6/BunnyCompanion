using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 本地 agent.md 长期记忆：
/// 结构化记忆 + 对话自动摘要压缩写入，超长时折叠旧摘要，供后续对话注入。
/// 路径：%LocalAppData%\BunnyCompanion\agent.md
/// </summary>
public sealed class LocalAgentMdStore
{
    public const int MaxFileChars = 28_000;
    public const int MaxRecentTurns = 24;
    public const int MaxDigestLineChars = 160;
    public const int CompressWhenTurnsExceed = 18;

    private readonly string _path;
    private readonly object _gate = new();

    public LocalAgentMdStore(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BunnyCompanion");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "agent.md");
        EnsureFileExists();
    }

    public string FilePath => _path;

    public void EnsureFileExists()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
                return;
            File.WriteAllText(_path, BuildSkeleton(structuredBody: "", rolling: [], recent: [], notes: ""), Encoding.UTF8);
        }
    }

    /// <summary>
    /// 用结构化记忆（人物/偏好/备忘）刷新 agent.md 的固定区，并保留滚动摘要与近期压缩。
    /// </summary>
    public void SyncStructured(string structuredMarkdownFromMemory)
    {
        lock (_gate)
        {
            var doc = ReadDocUnlocked();
            doc.Structured = string.IsNullOrWhiteSpace(structuredMarkdownFromMemory)
                ? "_（尚无结构化条目，聊多了会自动出现）_"
                : structuredMarkdownFromMemory.Trim();
            WriteDocUnlocked(doc);
        }
    }

    /// <summary>
    /// 写入一轮对话的压缩摘要；必要时自动折叠旧回合到「滚动摘要」。
    /// </summary>
    public void AppendTurnDigest(string userText, string assistantText, string? partnerName = null)
    {
        userText = CompressLine(userText, MaxDigestLineChars);
        assistantText = CompressLine(assistantText, MaxDigestLineChars);
        if (userText.Length == 0 && assistantText.Length == 0)
            return;

        var points = ExtractLocalPoints(userText, assistantText);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var block = new StringBuilder();
        block.AppendLine($"### {stamp}");
        if (!string.IsNullOrWhiteSpace(userText))
            block.AppendLine($"- 用户：{userText}");
        if (!string.IsNullOrWhiteSpace(assistantText))
            block.AppendLine($"- 小申：{assistantText}");
        if (points.Count > 0)
            block.AppendLine($"- 要点：{string.Join("；", points)}");
        block.AppendLine();

        lock (_gate)
        {
            var doc = ReadDocUnlocked();
            doc.Recent.Add(block.ToString().TrimEnd());
            if (doc.Recent.Count > CompressWhenTurnsExceed)
                CompressRecentUnlocked(doc);
            // 硬顶文件大小
            while (EstimateSize(doc) > MaxFileChars && doc.Recent.Count > 4)
            {
                CompressRecentUnlocked(doc, forceTake: Math.Max(4, doc.Recent.Count / 2));
            }
            while (EstimateSize(doc) > MaxFileChars && doc.Rolling.Count > 8)
                doc.Rolling.RemoveAt(0);
            WriteDocUnlocked(doc);
        }
    }

    /// <summary>注入系统提示：agent.md 全文截断版（优先结构化 + 滚动 + 最近几轮）。</summary>
    public string FormatForSystemPrompt(int maxChars = 6000)
    {
        lock (_gate)
        {
            var doc = ReadDocUnlocked();
            var sb = new StringBuilder();
            sb.AppendLine("# 本地 agent.md 长期记忆（自动摘要压缩，请自然使用）");
            sb.AppendLine();
            sb.AppendLine("## 结构化");
            sb.AppendLine(doc.Structured);
            sb.AppendLine();
            if (doc.Rolling.Count > 0)
            {
                sb.AppendLine("## 滚动摘要（旧对话已压缩）");
                foreach (var r in doc.Rolling.TakeLast(20))
                    sb.AppendLine("- " + r);
                sb.AppendLine();
            }
            if (doc.Recent.Count > 0)
            {
                sb.AppendLine("## 近期对话压缩");
                foreach (var r in doc.Recent.TakeLast(8))
                {
                    sb.AppendLine(r);
                    sb.AppendLine();
                }
            }
            if (!string.IsNullOrWhiteSpace(doc.UserNotes))
            {
                sb.AppendLine("## 用户手写备注");
                sb.AppendLine(doc.UserNotes.Trim());
            }

            var text = sb.ToString().Trim();
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n…（agent.md 过长，已截断注入；完整文件在本地）";
            return text;
        }
    }

    public string ReadRaw()
    {
        lock (_gate)
        {
            EnsureFileExists();
            try { return File.ReadAllText(_path); }
            catch { return ""; }
        }
    }

    public void ClearGeneratedKeepNotes()
    {
        lock (_gate)
        {
            var doc = ReadDocUnlocked();
            doc.Structured = "_（已清空，重新积累）_";
            doc.Rolling = [];
            doc.Recent = [];
            WriteDocUnlocked(doc);
        }
    }

    // ---------- internals ----------

    private sealed class AgentMdDoc
    {
        public string Structured { get; set; } = "";
        public List<string> Rolling { get; set; } = [];
        public List<string> Recent { get; set; } = [];
        public string UserNotes { get; set; } = "";
    }

    private void CompressRecentUnlocked(AgentMdDoc doc, int? forceTake = null)
    {
        var take = forceTake ?? Math.Max(6, doc.Recent.Count - MaxRecentTurns + 6);
        take = Math.Min(take, doc.Recent.Count);
        if (take <= 0) return;

        var old = doc.Recent.Take(take).ToList();
        doc.Recent = doc.Recent.Skip(take).ToList();

        // 把旧回合压成若干条滚动 bullet
        var joined = string.Join("\n", old);
        var bullets = CompressBlockToBullets(joined);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        if (bullets.Count == 0)
            doc.Rolling.Add($"{stamp}：压缩了 {take} 段早期对话");
        else
            foreach (var b in bullets.Take(8))
                doc.Rolling.Add($"{stamp} · {b}");

        if (doc.Rolling.Count > 40)
            doc.Rolling = doc.Rolling.TakeLast(40).ToList();
    }

    private static List<string> CompressBlockToBullets(string block)
    {
        var points = new List<string>();
        foreach (Match m in Regex.Matches(block, @"要点：(.+)"))
        {
            var p = m.Groups[1].Value.Trim();
            if (p.Length > 2) points.Add(CompressLine(p, 100));
        }
        foreach (Match m in Regex.Matches(block, @"用户：(.+)"))
        {
            var u = m.Groups[1].Value.Trim();
            if (u.Length > 8 && points.Count < 12)
                points.Add("曾说：" + CompressLine(u, 80));
        }
        // 去重
        return points.Distinct(StringComparer.Ordinal).Take(10).ToList();
    }

    private static List<string> ExtractLocalPoints(string user, string assistant)
    {
        var list = new List<string>();
        void AddIf(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.Length < 2) return;
            list.Add(CompressLine(s, 60));
        }

        // 简单关键词句
        foreach (Match m in Regex.Matches(user, @"(?:我(?:的)?(?:朋友|同事|喜欢|讨厌|住在|叫)|提醒我|备忘|星座|生日)[^。！？\n]{0,40}"))
            AddIf(m.Value);
        if (user.Contains("天气", StringComparison.Ordinal) || user.Contains("下雨", StringComparison.Ordinal))
            AddIf("聊了天气");
        if (user.Contains("代码", StringComparison.Ordinal) || user.Contains("bug", StringComparison.OrdinalIgnoreCase))
            AddIf("聊了代码/排错");
        if (assistant.Contains("已记下", StringComparison.Ordinal) || assistant.Contains("提醒", StringComparison.Ordinal))
            AddIf("小申确认了备忘/提醒");

        return list.Distinct(StringComparer.Ordinal).Take(5).ToList();
    }

    private static string CompressLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Replace("\r\n", "\n").Replace('\n', ' ').Replace('\t', ' ').Trim();
        t = Regex.Replace(t, @"\s+", " ");
        // 去掉附件大段
        var cut = t.IndexOf("【附件", StringComparison.Ordinal);
        if (cut >= 0) t = t[..cut].Trim();
        if (t.Length > max) t = t[..(max - 1)] + "…";
        return t;
    }

    private static int EstimateSize(AgentMdDoc doc) =>
        (doc.Structured?.Length ?? 0)
        + doc.Rolling.Sum(s => s.Length + 4)
        + doc.Recent.Sum(s => s.Length + 8)
        + (doc.UserNotes?.Length ?? 0)
        + 400;

    private AgentMdDoc ReadDocUnlocked()
    {
        EnsureFileExists();
        string raw;
        try { raw = File.ReadAllText(_path); }
        catch { return new AgentMdDoc(); }

        var doc = new AgentMdDoc();
        doc.Structured = SliceSection(raw, "## 结构化记忆", "## 滚动摘要") 
                         ?? SliceSection(raw, "## 结构化", "## 滚动") 
                         ?? "";
        doc.UserNotes = SliceSection(raw, "## 用户手写备注", null) ?? "";

        // 滚动摘要：列表行
        var rollingBody = SliceSection(raw, "## 滚动摘要", "## 近期对话压缩") ?? "";
        foreach (var line in rollingBody.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("- ", StringComparison.Ordinal))
                doc.Rolling.Add(t[2..].Trim());
            else if (t.Length > 2 && !t.StartsWith('_') && !t.StartsWith('#'))
                doc.Rolling.Add(t);
        }

        // 近期：### 块
        var recentBody = SliceSection(raw, "## 近期对话压缩", "## 用户手写备注") ?? "";
        if (!string.IsNullOrWhiteSpace(recentBody))
        {
            var parts = Regex.Split(recentBody.Trim(), @"(?=^### )", RegexOptions.Multiline)
                .Where(p => p.Trim().StartsWith("###", StringComparison.Ordinal))
                .Select(p => p.Trim())
                .ToList();
            doc.Recent = parts;
        }

        if (string.IsNullOrWhiteSpace(doc.Structured))
            doc.Structured = "_（尚无结构化条目）_";
        return doc;
    }

    private void WriteDocUnlocked(AgentMdDoc doc)
    {
        var text = BuildSkeleton(doc.Structured, doc.Rolling, doc.Recent, doc.UserNotes);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, text, Encoding.UTF8);
        File.Move(tmp, _path, true);
    }

    private static string BuildSkeleton(
        string structuredBody,
        List<string> rolling,
        List<string> recent,
        string notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 小申陪伴 · 本地长期记忆（agent.md）");
        sb.AppendLine();
        sb.AppendLine("> 本文件由程序**自动摘要压缩**写入，用于跨会话长期记忆。");
        sb.AppendLine("> 可在「用户手写备注」区自由补充；其它区可能被覆盖/折叠。");
        sb.AppendLine($"> 更新：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("## 结构化记忆");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(structuredBody) ? "_（尚无）_" : structuredBody.Trim());
        sb.AppendLine();
        sb.AppendLine("## 滚动摘要（旧对话已压缩）");
        sb.AppendLine();
        if (rolling.Count == 0)
            sb.AppendLine("_（尚无）_");
        else
            foreach (var r in rolling)
                sb.AppendLine("- " + r.TrimStart('-', ' '));
        sb.AppendLine();
        sb.AppendLine("## 近期对话压缩");
        sb.AppendLine();
        if (recent.Count == 0)
            sb.AppendLine("_（尚无）_");
        else
            foreach (var r in recent)
            {
                sb.AppendLine(r.Trim());
                sb.AppendLine();
            }
        sb.AppendLine("## 用户手写备注");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(notes)
            ? "_（可在此手写：偏好、禁忌、重要约定…）_"
            : notes.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    private static string? SliceSection(string raw, string startHeader, string? endHeader)
    {
        var i = raw.IndexOf(startHeader, StringComparison.Ordinal);
        if (i < 0) return null;
        i = raw.IndexOf('\n', i);
        if (i < 0) return "";
        i++;
        int j;
        if (endHeader is null)
            j = raw.Length;
        else
        {
            j = raw.IndexOf(endHeader, i, StringComparison.Ordinal);
            if (j < 0) j = raw.Length;
        }
        return raw[i..j].Trim();
    }
}
