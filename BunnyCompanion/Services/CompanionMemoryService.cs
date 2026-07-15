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
            return new MemoryIngestResult(0, 0, Array.Empty<string>());

        // 去掉附件块，避免把代码当记忆
        var cut = text.IndexOf("【附件文件", StringComparison.Ordinal);
        if (cut >= 0)
            text = text[..cut].Trim();
        if (text.Length > 800)
            text = text[..800];

        var peopleAdded = 0;
        var factsAdded = 0;
        var notes = new List<string>();

        lock (_gate)
        {
            // 显式：记住/记一下/帮我记
            if (Regex.IsMatch(text, @"记住|记一下|帮我记|别忘了|记下"))
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

            if (peopleAdded > 0 || factsAdded > 0)
            {
                _store.UpdatedAt = DateTime.UtcNow;
                // 锁内写盘
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
        }

        return new MemoryIngestResult(peopleAdded, factsAdded, notes);
    }

    /// <summary>注入系统提示的记忆摘要（可空）。</summary>
    public string FormatForSystemPrompt(int maxPeople = 12, int maxFacts = 16)
    {
        lock (_gate)
        {
            if (_store.People.Count == 0 && _store.Facts.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("# 长期记忆（本机沉淀，请自然使用，勿生硬复读整表）");
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

            sb.AppendLine("用记忆时要像熟人聊天：偶尔提起，不要每次都点名。");
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

    private void NormalizeUnlocked()
    {
        _store.People ??= [];
        _store.Facts ??= [];
        foreach (var p in _store.People)
        {
            p.Name = (p.Name ?? "").Trim();
            if (p.MentionCount < 1) p.MentionCount = 1;
        }

        _store.People = _store.People.Where(p => p.Name.Length is >= 1 and <= 20).ToList();
        _store.Facts = _store.Facts.Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
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

public sealed record MemoryIngestResult(int PeopleAddedOrUpdated, int FactsAdded, IReadOnlyList<string> Notes);
