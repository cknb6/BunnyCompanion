using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 长期陪伴记忆：偏好事实 + 聊过的人物。本地 JSON 持久化，卸载时一并清理。
/// </summary>
public sealed class CompanionMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private MemoryStore _store = new();

    public CompanionMemoryService(string? configDirectory = null)
    {
        var dir = configDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BunnyCompanion");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "companion_memory.json");
        Load();
        CompanionRuntime.Memory = this;
    }

    public string MemoryPath => _path;

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _store = new MemoryStore();
                    return;
                }

                var json = File.ReadAllText(_path);
                _store = JsonSerializer.Deserialize<MemoryStore>(json, JsonOptions) ?? new MemoryStore();
                NormalizeUnlocked();
            }
            catch
            {
                _store = new MemoryStore();
            }
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                NormalizeUnlocked();
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_store, JsonOptions));
                File.Move(tmp, _path, true);
            }
            catch
            {
                // 记忆写失败不阻断聊天
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _store = new MemoryStore();
            try
            {
                if (File.Exists(_path))
                    File.Delete(_path);
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>从一轮用户话中提取人物与事实（纯本地规则，可单测）。</summary>
    public MemoryIngestResult IngestUserUtterance(string? userText)
    {
        var text = (userText ?? string.Empty).Trim();
        if (text.Length == 0)
            return new MemoryIngestResult(0, 0, 0, Array.Empty<string>());

        // 去掉附件块，避免把代码当记忆
        var cut = text.IndexOf("【附件文件", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut].Trim();
        if (text.Length > 800)
            text = text[..800];

        var peopleAdded = 0;
        var factsAdded = 0;
        var memosAdded = 0;
        var notes = new List<string>();

        lock (_gate)
        {
            // 备忘/提醒（优先于普通「记住」）
            if (TryParseMemoUnlocked(text, out var memoText, out var due))
            {
                AddMemoUnlocked(memoText, due, source: "chat");
                memosAdded++;
                notes.Add(due is null ? "memo:" + memoText : $"memo@{due:MM-dd HH:mm}:{memoText}");
            }

            // 星座/生日写入画像
            if (Regex.IsMatch(text, @"星座|生日|出生"))
            {
                if (ZodiacService.TryParseDate(text, out var bd))
                {
                    _store.Birthday = bd;
                    var z = ZodiacService.FromDate(bd);
                    if (z is not null) _store.Zodiac = z.NameZh;
                    notes.Add("birthday:" + bd);
                }
                else
                {
                    foreach (var name in new[] { "白羊", "金牛", "双子", "巨蟹", "狮子", "处女", "天秤", "天蝎", "射手", "摩羯", "水瓶", "双鱼" })
                    {
                        if (text.Contains(name, StringComparison.Ordinal))
                        {
                            _store.Zodiac = name + "座";
                            notes.Add("zodiac:" + _store.Zodiac);
                            break;
                        }
                    }
                }
            }

            // 显式：记住/记一下/帮我记（非提醒句）
            if (memosAdded == 0 && Regex.IsMatch(text, @"记住|记一下|帮我记|别忘了|记下"))
            {
                var fact = Regex.Replace(text, @"^(请)?(帮我)?(记住|记一下|记下|别忘了)[，,：:\s]*", "", RegexOptions.IgnoreCase).Trim();
                if (fact.Length is >= 2 and <= 120 && AddFactUnlocked(fact, "explicit"))
                {
                    factsAdded++;
                    notes.Add("explicit:" + fact);
                }
            }

            // 人物：我朋友/同事… + 2～4 字名；叫 XXX；和 XXX 一起
            // 名字后遇到「要/会/说/下周…」等动词或标点即停，避免吞整句
            // 非贪婪取名，遇停用边界（要/会/下周/标点等）立即结束，避免「小明下周…」整段进库
            const string nameToken = @"[A-Za-z\u4e00-\u9fff]{2,4}?";
            const string nameBoundary = @"(?=\s|[，,。！？!?.]|要|会|说|来|去|能|想|下周|昨天|今天|明天|最近|一起|$)";
            var personPatterns = new[]
            {
                $@"(?:我(?:的)?(?:朋友|同事|同学|闺蜜|兄弟|姐妹|发小|老板|客户|室友|家人|对象|男朋友|女朋友|老公|老婆))\s*({nameToken}){nameBoundary}",
                $@"(?:叫|名叫|名字是)\s*({nameToken}){nameBoundary}",
                $@"(?:和|跟|与)\s*({nameToken})(?:一起|吃饭|见面|聊天|开会|工作|出去)",
            };
            foreach (var pat in personPatterns)
            {
                foreach (Match m in Regex.Matches(text, pat))
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!IsPlausiblePersonName(name))
                        continue;
                    var relation = GuessRelation(text, name);
                    var note = ExtractNoteNearName(text, name);
                    if (UpsertPersonUnlocked(name, relation, note))
                    {
                        peopleAdded++;
                        notes.Add("person:" + name);
                    }
                }
            }

            // 偏好：我喜欢/讨厌/习惯/住在/在…工作
            foreach (var pattern in new[]
                     {
                         @"我(?:其实)?(?:很)?喜欢(.{2,30})",
                         @"我(?:不喜欢|讨厌)(.{2,30})",
                         @"我(?:住在|在)(.{2,20})(?:上班|工作|上学|读书)?",
                         @"我(?:的)?爱好(?:是|：|:)(.{2,30})",
                         @"我(?:通常|一般|习惯)(.{2,30})",
                     })
            {
                var m = Regex.Match(text, pattern);
                if (!m.Success) continue;
                var fact = m.Value.Trim().TrimEnd('。', '！', '!', '?', '？', '，', ',');
                if (fact.Length is >= 4 and <= 80 && AddFactUnlocked(fact, "preference"))
                {
                    factsAdded++;
                    notes.Add("fact:" + fact);
                }
            }

            if (peopleAdded > 0 || factsAdded > 0 || memosAdded > 0 || notes.Count > 0)
            {
                _store.UpdatedAt = DateTime.UtcNow;
                PersistUnlocked();
            }
        }

        // 结构化变更后刷新本地 agent.md 固定区（锁外，避免死锁）
        if (peopleAdded > 0 || factsAdded > 0 || memosAdded > 0 || notes.Count > 0)
        {
            try { CompanionRuntime.SyncAgentMdFromMemory(); }
            catch { /* ignore */ }
        }

        return new MemoryIngestResult(peopleAdded, factsAdded, memosAdded, notes);
    }

    /// <summary>注入系统提示的记忆摘要（可空）。</summary>
    public string FormatForSystemPrompt(int maxPeople = 12, int maxFacts = 16, int maxMemos = 10)
    {
        lock (_gate)
        {
            if (_store.People.Count == 0 && _store.Facts.Count == 0
                && _store.Memos.Count == 0 && string.IsNullOrWhiteSpace(_store.Zodiac))
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("# 长期记忆（本机沉淀，请自然使用，勿生硬复读整表）");
            if (!string.IsNullOrWhiteSpace(_store.Zodiac) || _store.Birthday is not null)
            {
                sb.AppendLine("## 用户画像");
                if (_store.Birthday is { } bd)
                    sb.AppendLine($"- 生日: {bd:M月d日}");
                if (!string.IsNullOrWhiteSpace(_store.Zodiac))
                    sb.AppendLine($"- 星座: {_store.Zodiac}");
            }

            if (_store.People.Count > 0)
            {
                sb.AppendLine("## 认识的人");
                foreach (var p in _store.People
                             .OrderByDescending(x => x.MentionCount)
                             .ThenByDescending(x => x.LastMentionedUtc)
                             .Take(maxPeople))
                {
                    sb.Append("- ").Append(p.Name);
                    if (!string.IsNullOrWhiteSpace(p.Relation))
                        sb.Append("（").Append(p.Relation).Append('）');
                    if (!string.IsNullOrWhiteSpace(p.Note))
                        sb.Append("：").Append(p.Note);
                    sb.Append(" · 提及").Append(p.MentionCount).AppendLine("次");
                }
            }

            if (_store.Facts.Count > 0)
            {
                sb.AppendLine("## 关于用户");
                foreach (var f in _store.Facts
                             .OrderByDescending(x => x.UpdatedUtc)
                             .Take(maxFacts))
                    sb.Append("- ").AppendLine(f.Text);
            }

            var openMemos = _store.Memos.Where(m => !m.Done).OrderBy(m => m.DueAt ?? DateTime.MaxValue).Take(maxMemos).ToList();
            if (openMemos.Count > 0)
            {
                sb.AppendLine("## 未完成备忘/提醒");
                foreach (var m in openMemos)
                {
                    sb.Append("- ");
                    if (m.DueAt is { } due)
                        sb.Append(due.ToLocalTime().ToString("MM-dd HH:mm")).Append(' ');
                    sb.AppendLine(m.Text);
                }
            }

            sb.AppendLine("用记忆时要像熟人聊天：偶尔提起，不要每次都点名。到点的提醒要温柔说清楚。");
            return sb.ToString().Trim();
        }
    }

    public int PersonCount
    {
        get { lock (_gate) return _store.People.Count; }
    }

    public int FactCount
    {
        get { lock (_gate) return _store.Facts.Count; }
    }

    public int OpenMemoCount
    {
        get { lock (_gate) return _store.Memos.Count(m => !m.Done); }
    }

    public string? Zodiac
    {
        get { lock (_gate) return _store.Zodiac; }
    }

    public DateOnly? Birthday
    {
        get { lock (_gate) return _store.Birthday; }
    }

    public IReadOnlyList<PersonMemory> SnapshotPeople()
    {
        lock (_gate)
            return _store.People.Select(p => p.Clone()).ToList();
    }

    public IReadOnlyList<FactMemory> SnapshotFacts()
    {
        lock (_gate)
            return _store.Facts.Select(f => f.Clone()).ToList();
    }

    public IReadOnlyList<MemoItem> SnapshotMemos(bool includeDone = false)
    {
        lock (_gate)
            return _store.Memos.Where(m => includeDone || !m.Done).Select(m => m.Clone()).ToList();
    }

    public MemoItem AddMemo(string text, DateTime? dueLocal, string source = "api")
    {
        lock (_gate)
        {
            var item = AddMemoUnlocked(text, dueLocal, source);
            PersistUnlocked();
            return item.Clone();
        }
    }

    public string ListMemosText(bool includeDone = false)
    {
        var list = SnapshotMemos(includeDone);
        if (list.Count == 0) return "（暂无备忘）";
        var sb = new StringBuilder();
        foreach (var m in list.OrderBy(x => x.DueAt ?? DateTime.MaxValue))
        {
            var flag = m.Done ? "[已完成]" : m.Fired ? "[已提醒]" : "[待办]";
            var due = m.DueAt is { } d ? d.ToLocalTime().ToString("MM-dd HH:mm") : "不限时";
            sb.AppendLine($"{flag} {due} · {m.Text} · id={m.Id[..8]}");
        }
        return sb.ToString().Trim();
    }

    public bool CompleteMemo(string idOrPrefix)
    {
        lock (_gate)
        {
            var m = _store.Memos.FirstOrDefault(x =>
                x.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)
                || x.Text.Contains(idOrPrefix, StringComparison.OrdinalIgnoreCase));
            if (m is null) return false;
            m.Done = true;
            PersistUnlocked();
            return true;
        }
    }

    /// <summary>取出到期且未提醒的备忘（标记 Fired，供气泡/对话使用）。</summary>
    public IReadOnlyList<MemoItem> PopDueMemos(DateTime? nowLocal = null)
    {
        var now = nowLocal ?? DateTime.Now;
        lock (_gate)
        {
            var due = _store.Memos
                .Where(m => !m.Done && !m.Fired && m.DueAt is { } d && d.ToLocalTime() <= now)
                .OrderBy(m => m.DueAt)
                .Take(5)
                .ToList();
            foreach (var m in due)
                m.Fired = true;
            if (due.Count > 0)
                PersistUnlocked();
            return due.Select(m => m.Clone()).ToList();
        }
    }

    public string? TryPickFactBubble(string partnerName, double probability = 0.18, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (rng.NextDouble() >= Math.Clamp(probability, 0, 1))
            return null;
        FactMemory? fact;
        lock (_gate)
        {
            if (_store.Facts.Count == 0) return null;
            fact = _store.Facts[rng.Next(_store.Facts.Count)];
        }
        var lines = new[]
        {
            $"我还记得：{fact.Text}",
            $"对了，你说过「{TrimFact(fact.Text)}」——我没忘。",
            $"{partnerName}，关于「{TrimFact(fact.Text)}」还算数吗？",
        };
        return lines[rng.Next(lines.Length)];
    }

    public string? TryPickMemoNudgeBubble(string partnerName, double probability = 0.2, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (rng.NextDouble() >= Math.Clamp(probability, 0, 1))
            return null;
        MemoItem? memo;
        lock (_gate)
        {
            var open = _store.Memos.Where(m => !m.Done).ToList();
            if (open.Count == 0) return null;
            memo = open.OrderBy(m => m.DueAt ?? DateTime.MaxValue).First();
        }
        if (memo.DueAt is { } due)
        {
            var local = due.ToLocalTime();
            if (local > DateTime.Now.AddHours(12))
                return $"{partnerName}，备忘「{memo.Text}」记在 {local:MM-dd HH:mm}，我到点喊你。";
            return $"别忘了：{memo.Text}" + (local > DateTime.Now ? $"（约 {local:HH:mm}）" : "");
        }
        return $"待办还在：{memo.Text}";
    }

    /// <summary>
    /// 偶尔气泡：无人物记忆返回 null；有人物时以 probability 概率抽出一句印象提醒。
    /// 默认 0.28，保证「有时」而非次次。
    /// </summary>
    public string? TryPickPersonBubble(string partnerName, double probability = 0.28, Random? rng = null)
    {
        rng ??= Random.Shared;
        if (rng.NextDouble() >= Math.Clamp(probability, 0, 1))
            return null;

        PersonMemory? person;
        lock (_gate)
        {
            if (_store.People.Count == 0)
                return null;
            person = _store.People
                .OrderByDescending(p => p.MentionCount)
                .ThenByDescending(p => p.LastMentionedUtc)
                .Skip(rng.Next(0, Math.Min(5, _store.People.Count)))
                .FirstOrDefault() ?? _store.People[rng.Next(_store.People.Count)];
        }

        var templates = new[]
        {
            $"对了，{partnerName}上次聊到的{person.Name}，最近还好吗？",
            $"突然想到{person.Name}…你们的故事我还记得一点点。",
            $"有空也可以跟{person.Name}说说话，关系要轻轻养。",
            $"{person.Name}在你心里挺重要的吧，我记下了。",
            $"如果今天见到{person.Name}，记得对自己也好一点。",
        };
        if (!string.IsNullOrWhiteSpace(person.Relation))
        {
            templates =
            [
                ..templates,
                $"你的{person.Relation}{person.Name}，我这边有印象哦。",
                $"想起你说过的{person.Relation}{person.Name}了～",
            ];
        }

        return templates[rng.Next(templates.Length)];
    }

    // ---------- internals ----------

    private bool UpsertPersonUnlocked(string name, string? relation, string? note)
    {
        var existing = _store.People.FirstOrDefault(p =>
            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            if (_store.People.Count >= 40)
                _store.People = _store.People
                    .OrderByDescending(p => p.MentionCount)
                    .Take(36)
                    .ToList();
            _store.People.Add(new PersonMemory
            {
                Name = name,
                Relation = relation,
                Note = note,
                MentionCount = 1,
                LastMentionedUtc = DateTime.UtcNow,
            });
            return true;
        }

        existing.MentionCount++;
        existing.LastMentionedUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(relation) && string.IsNullOrWhiteSpace(existing.Relation))
            existing.Relation = relation;
        if (!string.IsNullOrWhiteSpace(note))
            existing.Note = MergeNote(existing.Note, note);
        return true;
    }

    private bool AddFactUnlocked(string text, string source)
    {
        text = text.Trim();
        if (_store.Facts.Any(f => f.Text.Equals(text, StringComparison.OrdinalIgnoreCase)))
            return false;
        // 近似去重
        if (_store.Facts.Any(f => f.Text.Contains(text, StringComparison.OrdinalIgnoreCase)
                                  || text.Contains(f.Text, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_store.Facts.Count >= 60)
            _store.Facts = _store.Facts.OrderByDescending(f => f.UpdatedUtc).Take(50).ToList();
        _store.Facts.Add(new FactMemory
        {
            Text = text,
            Source = source,
            UpdatedUtc = DateTime.UtcNow,
        });
        return true;
    }

    private void PersistUnlocked()
    {
        try
        {
            NormalizeUnlocked();
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_store, JsonOptions));
            File.Move(tmp, _path, true);
        }
        catch { /* ignore */ }
    }

    private MemoItem AddMemoUnlocked(string text, DateTime? dueLocal, string source)
    {
        text = (text ?? "").Trim();
        if (text.Length > 160) text = text[..160];
        if (_store.Memos.Count >= 80)
            _store.Memos = _store.Memos.Where(m => !m.Done).OrderByDescending(m => m.CreatedUtc).Take(60).ToList();
        var item = new MemoItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Text = text,
            DueAt = dueLocal?.ToUniversalTime(),
            CreatedUtc = DateTime.UtcNow,
            Source = source,
        };
        _store.Memos.Add(item);
        return item;
    }

    /// <summary>解析「提醒我…」「备忘…」「X分钟后…」等。</summary>
    public static bool TryParseMemo(string text, out string memoText, out DateTime? dueLocal) =>
        TryParseMemoCore(text, out memoText, out dueLocal);

    private static bool TryParseMemoUnlocked(string text, out string memoText, out DateTime? dueLocal) =>
        TryParseMemoCore(text, out memoText, out dueLocal);

    private static bool TryParseMemoCore(string text, out string memoText, out DateTime? dueLocal)
    {
        memoText = "";
        dueLocal = null;
        var t = text.Trim();
        if (!Regex.IsMatch(t, @"提醒|备忘|待办|叫我|别忘了提醒|闹钟"))
            return false;

        var now = DateTime.Now;

        // N分钟后 / N小时后 / 半小时后
        var m = Regex.Match(t, @"(?:过)?(\d{1,3})\s*分钟后");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var mins))
            dueLocal = now.AddMinutes(Math.Clamp(mins, 1, 24 * 60));
        else if (Regex.IsMatch(t, @"半小时后|半个小时后"))
            dueLocal = now.AddMinutes(30);
        else if ((m = Regex.Match(t, @"(?:过)?(\d{1,2})\s*小时后")).Success && int.TryParse(m.Groups[1].Value, out var hours))
            dueLocal = now.AddHours(Math.Clamp(hours, 1, 72));
        else
        {
            // 明天/后天 + 可选时间
            var dayOffset = 0;
            if (t.Contains("后天", StringComparison.Ordinal)) dayOffset = 2;
            else if (t.Contains("明天", StringComparison.Ordinal)) dayOffset = 1;

            var hour = -1;
            var minute = 0;
            // 先匹配「下午3点」等，避免「3点」单独命中丢掉上下午
            if ((m = Regex.Match(t, @"(上午|中午|下午|晚上|早上|傍晚)\s*(\d{1,2})\s*点(?:\s*(\d{1,2})\s*分?)?")).Success)
            {
                hour = int.Parse(m.Groups[2].Value);
                if (m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var mi0))
                    minute = mi0;
                var period = m.Groups[1].Value;
                if ((period is "下午" or "晚上" or "傍晚") && hour < 12) hour += 12;
                if (period == "中午" && hour < 11) hour = 12;
                if (period is "上午" or "早上" && hour == 12) hour = 0;
            }
            else if ((m = Regex.Match(t, @"(\d{1,2})\s*[:：]\s*(\d{1,2})")).Success)
            {
                hour = int.Parse(m.Groups[1].Value);
                minute = int.Parse(m.Groups[2].Value);
            }
            else if ((m = Regex.Match(t, @"(\d{1,2})\s*点(?:\s*(\d{1,2})\s*分?)?")).Success)
            {
                hour = int.Parse(m.Groups[1].Value);
                if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var mi1))
                    minute = mi1;
                // 口语「3点」若全文带下午/晚上则校正
                if (t.Contains("下午", StringComparison.Ordinal) || t.Contains("晚上", StringComparison.Ordinal)
                    || t.Contains("傍晚", StringComparison.Ordinal))
                {
                    if (hour < 12) hour += 12;
                }
            }

            if (hour is >= 0 and <= 23)
            {
                var day = now.Date.AddDays(dayOffset);
                dueLocal = day.AddHours(hour).AddMinutes(Math.Clamp(minute, 0, 59));
                if (dueLocal <= now && dayOffset == 0)
                    dueLocal = dueLocal.Value.AddDays(1);
            }
            else if (dayOffset > 0)
            {
                dueLocal = now.Date.AddDays(dayOffset).AddHours(9); // 默认明天上午9点
            }
        }

        // 正文：去掉提醒前缀与时间词
        memoText = t;
        memoText = Regex.Replace(memoText, @"^(请)?(帮我)?(提醒我|提醒|备忘录?|待办|记下?待办)[：:\s]*", "");
        memoText = Regex.Replace(memoText, @"(?:过)?\d{1,3}\s*分钟后|(?:过)?\d{1,2}\s*小时后|半(?:个)?小时后", "");
        memoText = Regex.Replace(memoText, @"明天|后天|今天|上午|中午|下午|晚上|早上|傍晚", "");
        memoText = Regex.Replace(memoText, @"\d{1,2}\s*[:：点时]\s*\d{0,2}\s*分?", "");
        memoText = memoText.Trim(' ', '，', ',', '。', '：', ':', '、');
        if (memoText.Length < 1)
            memoText = "你设的提醒";
        if (memoText.Length > 120)
            memoText = memoText[..120];
        return true;
    }

    private static string TrimFact(string text) =>
        text.Length <= 28 ? text : text[..27] + "…";

    private void NormalizeUnlocked()
    {
        _store.People ??= [];
        _store.Facts ??= [];
        _store.Memos ??= [];
        foreach (var p in _store.People)
        {
            p.Name = (p.Name ?? "").Trim();
            if (p.MentionCount < 1) p.MentionCount = 1;
        }

        _store.People = _store.People.Where(p => p.Name.Length is >= 1 and <= 20).ToList();
        _store.Facts = _store.Facts.Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        _store.Memos = _store.Memos.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToList();
    }

    private static string? GuessRelation(string text, string name)
    {
        string[] keys =
        [
            "朋友", "同事", "同学", "闺蜜", "兄弟", "姐妹", "发小", "老板", "客户", "室友",
            "妈妈", "爸爸", "哥哥", "姐姐", "弟弟", "妹妹", "男朋友", "女朋友", "老公", "老婆", "对象",
        ];
        foreach (var k in keys)
        {
            if (text.Contains(k + name, StringComparison.Ordinal) || text.Contains(name + k, StringComparison.Ordinal)
                                                                  || text.Contains("我的" + k, StringComparison.Ordinal)
                                                                  || text.Contains("我" + k, StringComparison.Ordinal))
                return k;
        }

        return null;
    }

    private static string? ExtractNoteNearName(string text, string name)
    {
        var idx = text.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = Math.Max(0, idx - 12);
        var len = Math.Min(text.Length - start, name.Length + 36);
        var slice = text.Substring(start, len).Trim();
        return slice.Length > 2 ? slice : null;
    }

    private static string MergeNote(string? old, string? neu)
    {
        if (string.IsNullOrWhiteSpace(old)) return neu ?? "";
        if (string.IsNullOrWhiteSpace(neu)) return old;
        if (old.Contains(neu, StringComparison.OrdinalIgnoreCase)) return old;
        var m = old + "；" + neu;
        return m.Length > 120 ? m[..120] : m;
    }

    private static bool IsPlausiblePersonName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length is < 1 or > 12) return false;
        // 过滤常见非人名
        string[] ban =
        [
            "什么", "怎么", "这样", "那样", "今天", "明天", "昨天", "这里", "那里", "一个", "这个", "那个",
            "自己", "大家", "我们", "你们", "他们", "时候", "东西", "问题", "工作", "文件", "电脑", "桌面",
            "天气", "位置", "地方", "公司", "学校", "项目", "代码", "程序", "宝宝", "小申", "你好",
        ];
        if (ban.Any(b => name.Equals(b, StringComparison.OrdinalIgnoreCase)))
            return false;
        return Regex.IsMatch(name, @"^[\u4e00-\u9fffA-Za-z]{1,12}$");
    }

    private static string FirstNonEmpty(params string[] xs) =>
        xs.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim() ?? "";

    private sealed class MemoryStore
    {
        public List<PersonMemory> People { get; set; } = [];
        public List<FactMemory> Facts { get; set; } = [];
        public List<MemoItem> Memos { get; set; } = [];
        public string? Zodiac { get; set; }
        public DateOnly? Birthday { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}

public sealed class PersonMemory
{
    public string Name { get; set; } = "";
    public string? Relation { get; set; }
    public string? Note { get; set; }
    public int MentionCount { get; set; } = 1;
    public DateTime LastMentionedUtc { get; set; } = DateTime.UtcNow;

    public PersonMemory Clone() => new()
    {
        Name = Name,
        Relation = Relation,
        Note = Note,
        MentionCount = MentionCount,
        LastMentionedUtc = LastMentionedUtc,
    };
}

public sealed class FactMemory
{
    public string Text { get; set; } = "";
    public string Source { get; set; } = "auto";
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public FactMemory Clone() => new()
    {
        Text = Text,
        Source = Source,
        UpdatedUtc = UpdatedUtc,
    };
}

public sealed class MemoItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>UTC 到期时间；null 表示不限时待办。</summary>
    public DateTime? DueAt { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public bool Done { get; set; }
    public bool Fired { get; set; }
    public string Source { get; set; } = "chat";

    public MemoItem Clone() => new()
    {
        Id = Id,
        Text = Text,
        DueAt = DueAt,
        CreatedUtc = CreatedUtc,
        Done = Done,
        Fired = Fired,
        Source = Source,
    };
}

public sealed record MemoryIngestResult(
    int PeopleAddedOrUpdated,
    int FactsAdded,
    int MemosAdded,
    IReadOnlyList<string> Notes);
