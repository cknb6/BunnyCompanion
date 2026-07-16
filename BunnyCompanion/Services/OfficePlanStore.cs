using System.Text;
using System.Text.Json;

namespace BunnyCompanion.Services;

/// <summary>办公计划单步状态。</summary>
public enum OfficePlanStepStatus
{
    Pending,
    Done,
    Failed,
    Skipped,
}

/// <summary>办公计划一步。</summary>
public sealed class OfficePlanStep
{
    public int Index { get; set; }
    public string Text { get; set; } = "";
    public OfficePlanStepStatus Status { get; set; } = OfficePlanStepStatus.Pending;
    public string? Note { get; set; }
}

/// <summary>
/// 会话级办公计划（Claude Code Todo 风格）：plan_set / plan_tick / plan_status。
/// 进程内单例，可选落盘到 LocalAppData。
/// </summary>
public sealed class OfficePlanStore
{
    private readonly object _gate = new();
    private string _title = "";
    private readonly List<OfficePlanStep> _steps = [];
    private readonly string _persistPath;

    public OfficePlanStore(string? persistPath = null)
    {
        _persistPath = persistPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BunnyCompanion",
            "office_plan.json");
        TryLoad();
    }

    public bool HasPlan
    {
        get
        {
            lock (_gate)
                return _steps.Count > 0;
        }
    }

    /// <summary>
    /// 设置/重置计划。steps 为换行或 | 或 JSON 数组分隔的步骤文本。
    /// </summary>
    public string SetPlan(string title, string stepsRaw)
    {
        var lines = ParseSteps(stepsRaw);
        if (lines.Count == 0)
            return "错误：计划步骤为空。请提供 3～12 条可执行步骤。";

        if (lines.Count > 16)
            lines = lines.Take(16).ToList();

        lock (_gate)
        {
            _title = string.IsNullOrWhiteSpace(title) ? "未命名任务" : title.Trim();
            _steps.Clear();
            for (var i = 0; i < lines.Count; i++)
            {
                _steps.Add(new OfficePlanStep
                {
                    Index = i + 1,
                    Text = lines[i],
                    Status = OfficePlanStepStatus.Pending,
                });
            }

            TrySaveUnlocked();
        }

        return StatusText() + "\n（计划已建立。完成一步后调用 plan_tick；全部完成后再给用户最终总结。）";
    }

    /// <summary>
    /// 勾选某步。index 从 1 开始；status: done|failed|skip|pending。
    /// </summary>
    public string Tick(int index, string status, string? note = null)
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
                return "当前没有计划。请先 plan_set。";

            var step = _steps.FirstOrDefault(s => s.Index == index);
            if (step is null)
                return $"错误：没有第 {index} 步（共 {_steps.Count} 步）。";

            step.Status = ParseStatus(status);
            if (!string.IsNullOrWhiteSpace(note))
                step.Note = note.Trim();
            TrySaveUnlocked();
        }

        return StatusText();
    }

    public string StatusText()
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
                return "（暂无办公计划）";

            var sb = new StringBuilder();
            sb.AppendLine($"【办公计划】{_title}");
            var done = _steps.Count(s => s.Status == OfficePlanStepStatus.Done);
            var failed = _steps.Count(s => s.Status == OfficePlanStepStatus.Failed);
            var skip = _steps.Count(s => s.Status == OfficePlanStepStatus.Skipped);
            var pending = _steps.Count(s => s.Status == OfficePlanStepStatus.Pending);
            sb.AppendLine($"进度: 完成{done} / 失败{failed} / 跳过{skip} / 待做{pending} · 共{_steps.Count}步");

            foreach (var s in _steps)
            {
                var mark = s.Status switch
                {
                    OfficePlanStepStatus.Done => "[x]",
                    OfficePlanStepStatus.Failed => "[!]",
                    OfficePlanStepStatus.Skipped => "[-]",
                    _ => "[ ]",
                };
                sb.Append(mark).Append(' ').Append(s.Index).Append(". ").Append(s.Text);
                if (!string.IsNullOrWhiteSpace(s.Note))
                    sb.Append(" — ").Append(s.Note);
                sb.AppendLine();
            }

            if (pending == 0 && failed == 0)
                sb.AppendLine("全部步骤已完成或跳过，可以给用户做最终交付总结。");
            else if (pending > 0)
                sb.AppendLine("还有待做步骤：继续调用工具推进，不要提前收口。");

            return sb.ToString().Trim();
        }
    }

    /// <summary>注入 system 的短摘要（有计划时）。</summary>
    public string? FormatForSystemPrompt()
    {
        lock (_gate)
        {
            if (_steps.Count == 0)
                return null;
        }

        return "# 当前办公计划（须对照推进）\n" + StatusText();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _title = "";
            _steps.Clear();
            TrySaveUnlocked();
        }
    }

    /// <summary>解析步骤列表：JSON 数组 / 换行 / | 分隔。</summary>
    public static List<string> ParseSteps(string? raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var text = raw.Trim();
        // JSON 数组
        if (text.StartsWith('[') && text.EndsWith(']'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var s = el.ValueKind == JsonValueKind.String
                            ? el.GetString()
                            : el.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            result.Add(s.Trim());
                    }

                    if (result.Count > 0)
                        return result;
                }
            }
            catch
            {
                // fall through
            }
        }

        // 换行
        var parts = text.Split(['\r', '\n', '|', ';', '；'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            var line = p.Trim();
            // 去掉 "1. " "1) " "- "
            if (line.Length > 2 && char.IsDigit(line[0]))
            {
                var i = 0;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] is '.' or ')' or '、'))
                    i++;
                line = line[i..].Trim();
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("· ", StringComparison.Ordinal))
                line = line[2..].Trim();
            if (!string.IsNullOrWhiteSpace(line))
                result.Add(line);
        }

        return result;
    }

    public static OfficePlanStepStatus ParseStatus(string? status)
    {
        var s = (status ?? "done").Trim().ToLowerInvariant();
        return s switch
        {
            "done" or "ok" or "complete" or "completed" or "完成" or "x" => OfficePlanStepStatus.Done,
            "failed" or "fail" or "error" or "失败" or "!" => OfficePlanStepStatus.Failed,
            "skip" or "skipped" or "跳过" or "-" => OfficePlanStepStatus.Skipped,
            "pending" or "todo" or "待做" or "" => OfficePlanStepStatus.Pending,
            _ => OfficePlanStepStatus.Done,
        };
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(_persistPath))
                return;
            var json = File.ReadAllText(_persistPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            _steps.Clear();
            if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var idx = el.TryGetProperty("index", out var i) ? i.GetInt32() : _steps.Count + 1;
                    var text = el.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    var st = el.TryGetProperty("status", out var s)
                        ? ParseStatus(s.GetString())
                        : OfficePlanStepStatus.Pending;
                    var note = el.TryGetProperty("note", out var n) ? n.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text))
                        continue;
                    _steps.Add(new OfficePlanStep { Index = idx, Text = text, Status = st, Note = note });
                }
            }
        }
        catch
        {
            // ignore corrupt
        }
    }

    private void TrySaveUnlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_persistPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var obj = new
            {
                title = _title,
                steps = _steps.Select(s => new
                {
                    index = s.Index,
                    text = s.Text,
                    status = s.Status.ToString().ToLowerInvariant(),
                    note = s.Note,
                }).ToList(),
            };
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignore
        }
    }
}
