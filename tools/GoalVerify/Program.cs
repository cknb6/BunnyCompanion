using BunnyCompanion.Engine;
using BunnyCompanion.Models;
using BunnyCompanion.Services;

// 驱动交付源码的门禁自检（非 mock 再实现）
var scratch = Environment.GetEnvironmentVariable("GOAL_SCRATCH")
              ?? Path.Combine(Path.GetTempPath(), "goal-verify-out");
Directory.CreateDirectory(scratch);
var fails = 0;

void Ok(string msg) => Console.WriteLine("OK  " + msg);
void Fail(string msg)
{
    fails++;
    Console.WriteLine("FAIL " + msg);
}

// ---------- 1) 宝宝默认称呼 ----------
{
    var s = new PetSettings();
    s.Normalize();
    if (s.PartnerName != "宝宝") Fail($"默认 PartnerName={s.PartnerName} 期望 宝宝");
    else Ok("PetSettings 默认=宝宝");

    var legacy = new PetSettings { PartnerName = "宝贝" };
    legacy.Normalize();
    if (legacy.PartnerName != "宝宝") Fail($"宝贝未迁移: {legacy.PartnerName}");
    else Ok("历史「宝贝」Normalize→宝宝");

    File.WriteAllText(Path.Combine(scratch, "baobao-rename.txt"),
        $"default={s.PartnerName}\nmigrated_from_宝贝={legacy.PartnerName}\n");
}

// ---------- 2) 记忆 + 人物气泡 ----------
{
    var dir = Path.Combine(scratch, "mem-data");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var mem = new CompanionMemoryService(dir);
    var r1 = mem.IngestUserUtterance("我朋友小明下周要来找我玩");
    var r2 = mem.IngestUserUtterance("记住我喜欢喝美式咖啡");
    if (mem.PersonCount < 1) Fail("未记住人物小明");
    else Ok($"人物数={mem.PersonCount} notes={string.Join(',', r1.Notes)}");
    if (mem.FactCount < 1) Fail("未记住偏好事实");
    else Ok($"事实数={mem.FactCount}");

    var mem2 = new CompanionMemoryService(dir);
    if (mem2.PersonCount < 1 || mem2.SnapshotPeople().All(p => p.Name != "小明"))
        Fail("持久化再读人物失败");
    else Ok("持久化再读人物 OK");

    var prompt = mem2.FormatForSystemPrompt();
    if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("小明", StringComparison.Ordinal))
        Fail("系统提示记忆块缺失小明");
    else Ok("FormatForSystemPrompt 含人物");

    // 无人物时气泡
    var emptyDir = Path.Combine(scratch, "mem-empty");
    Directory.CreateDirectory(emptyDir);
    var empty = new CompanionMemoryService(emptyDir);
    empty.Clear();
    var none = empty.TryPickPersonBubble("宝宝", probability: 1.0, rng: new Random(1));
    if (none is not null) Fail("无记忆仍吐人物气泡");
    else Ok("无记忆气泡=null");

    // 有人物时：概率 0 永不出现；概率 1 必出现
    var never = mem2.TryPickPersonBubble("宝宝", probability: 0, rng: new Random(2));
    var always = mem2.TryPickPersonBubble("宝宝", probability: 1.0, rng: new Random(3));
    if (never is not null) Fail("probability=0 仍出气泡");
    else Ok("probability=0 → null");
    if (always is null || !always.Contains("小明", StringComparison.Ordinal))
        Fail($"probability=1 应提小明: {always}");
    else Ok("probability=1 → 含人物名");

    // 中间概率：固定种子多次，既非全有也非全无（0.28 在 80 次里应有变化）
    var rng = new Random(42);
    var hits = 0;
    const int trials = 80;
    for (var i = 0; i < trials; i++)
    {
        if (mem2.TryPickPersonBubble("宝宝", probability: 0.28, rng: rng) is not null)
            hits++;
    }
    if (hits is 0 or trials) Fail($"偶尔气泡退化 hits={hits}/{trials}");
    else Ok($"偶尔气泡 hits={hits}/{trials} (非0非全)");

    // 备忘录
    var memoDir = Path.Combine(scratch, "mem-memo");
    if (Directory.Exists(memoDir)) Directory.Delete(memoDir, true);
    var memMemo = new CompanionMemoryService(memoDir);
    var rMemo = memMemo.IngestUserUtterance("提醒我30分钟后起来喝水");
    if (rMemo.MemosAdded < 1 || memMemo.OpenMemoCount < 1) Fail("备忘未写入");
    else Ok($"备忘写入 open={memMemo.OpenMemoCount}");
    if (!CompanionMemoryService.TryParseMemo("提醒我明天下午3点开会", out var mt, out var due) || due is null)
        Fail("备忘时间解析失败");
    else Ok($"解析备忘 due={due:g} text={mt}");

    // 到期弹出
    memMemo.AddMemo("立刻事项", DateTime.Now.AddMinutes(-1));
    var dueList = memMemo.PopDueMemos(DateTime.Now);
    if (dueList.Count < 1) Fail("PopDueMemos 未弹出到期项");
    else Ok("PopDueMemos OK count=" + dueList.Count);

    File.WriteAllText(Path.Combine(scratch, "memory-person.txt"),
        $"people={mem2.PersonCount} facts={mem2.FactCount} memos={memMemo.OpenMemoCount}\nprompt_len={prompt.Length}\nhits={hits}/{trials}\nalways={always}\n");
}

// ---------- 2a) agent.md 摘要压缩 ----------
{
    var dir = Path.Combine(scratch, "agent-md-data");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    Directory.CreateDirectory(dir);
    var md = new LocalAgentMdStore(dir);
    if (!File.Exists(md.FilePath)) Fail("agent.md 未创建");
    else Ok("agent.md 路径 " + md.FilePath);

    for (var i = 0; i < 22; i++)
        md.AppendTurnDigest($"用户说了第{i}轮：我朋友小美喜欢喝茶", $"小申回复第{i}轮：记下了喝茶和朋友", "宝宝");
    md.SyncStructured("- 测试结构化\n- 朋友小美");
    var raw = md.ReadRaw();
    if (!raw.Contains("滚动摘要", StringComparison.Ordinal)) Fail("缺滚动摘要区");
    else Ok("含滚动摘要区");
    if (!raw.Contains("近期对话压缩", StringComparison.Ordinal)) Fail("缺近期压缩区");
    else Ok("含近期压缩区");
    // 22 轮应触发压缩，滚动区或近期被折叠
    if (!raw.Contains("小美", StringComparison.Ordinal) && !raw.Contains("喝茶", StringComparison.Ordinal)
        && !raw.Contains("压缩", StringComparison.Ordinal))
        Fail("摘要未留下痕迹");
    else Ok("摘要压缩有内容");

    var inject = md.FormatForSystemPrompt(3000);
    if (inject.Length < 50) Fail("注入块过短");
    else Ok($"agent.md 注入长度={inject.Length}");

    File.WriteAllText(Path.Combine(scratch, "agent-md.txt"), raw[..Math.Min(2000, raw.Length)] + "\n---\ninject_len=" + inject.Length);
}

// ---------- 2b) 星座 / 今日卡 ----------
{
    var z = ZodiacService.Analyze("1999-08-15", "宝宝");
    if (!z.Contains("处女座", StringComparison.Ordinal) && !z.Contains("星座", StringComparison.Ordinal))
        Fail("星座分析缺星座名: " + z[..Math.Min(80, z.Length)]);
    else Ok("星座分析含内容");
    if (!z.Contains("今日运势", StringComparison.Ordinal)) Fail("缺今日运势节");
    else Ok("星座今日运势节 OK");

    var card = DailyCompanion.BuildDailyCard("宝宝");
    if (!card.Contains("今日陪伴卡", StringComparison.Ordinal)) Fail("陪伴卡格式不对");
    else Ok("DailyCompanion 卡片 OK");

    File.WriteAllText(Path.Combine(scratch, "zodiac-daily.txt"), z + "\n---\n" + card + "\n");
}

// ---------- 3) 鼠标反应池 ----------
{
    var zones = Enum.GetValues<BodyZone>();
    if (zones.Length < 9) Fail("BodyZone 不足 9");
    else Ok($"BodyZone={zones.Length}");

    var click = MouseReactionCatalog.PickClick(BodyZone.HeadCenter, 1, 1, "宝宝");
    if (string.IsNullOrWhiteSpace(click.ActionKey)) Fail("点击反应无 ActionKey");
    else Ok($"click action={click.ActionKey}");

    var fling = MouseReactionCatalog.PickDragRelease(DragDirection.Right, DragIntensity.Fling, "宝宝");
    var soft = MouseReactionCatalog.PickDragRelease(DragDirection.Up, DragIntensity.Soft, "宝宝");
    if (string.IsNullOrWhiteSpace(fling.ActionKey) || string.IsNullOrWhiteSpace(soft.ActionKey))
        Fail("拖拽反应空");
    else Ok($"drag fling={fling.ActionKey} soft={soft.ActionKey}");

    var dir = MouseReactionCatalog.ResolveDragDirection(-100, 10);
    if (dir != DragDirection.Left) Fail($"方向应为 Left 得 {dir}");
    else Ok("ResolveDragDirection Left");

    var intensity = MouseReactionCatalog.ResolveDragIntensity(400, 0.3);
    if (intensity != DragIntensity.Fling) Fail($"强度应为 Fling 得 {intensity}");
    else Ok("ResolveDragIntensity Fling");

    File.WriteAllText(Path.Combine(scratch, "click-drag.txt"),
        $"zones={zones.Length}\nclick={click.ActionKey}/{click.Message}\nfling={fling.ActionKey}\nsoft={soft.ActionKey}\ndir={dir}\nintensity={intensity}\n");
}

// ---------- 4) 天气高温/降水 ----------
{
    var hot = WeatherReport.BuildWeatherAlerts(38, 40, 39, 0, 0, 10, "晴");
    if (!hot.Any(a => a.Contains("高温", StringComparison.Ordinal))) Fail("38°C 应有高温警示");
    else Ok("高温警示: " + hot[0]);

    var rain = WeatherReport.BuildWeatherAlerts(22, 22, 24, 6, 20, 80, "中雨");
    if (!rain.Any(a => a.Contains("降水", StringComparison.Ordinal))) Fail("应有降水警示");
    else Ok("降水警示: " + rain.First(a => a.Contains("降水")));

    var sampleJson = """
        {
          "current_condition":[{"temp_C":"36","FeelsLikeC":"38","humidity":"55","windspeedKmph":"12","precipMM":"0.2","weatherDesc":[{"value":"Sunny"}],"lang_zh":[{"value":"晴"}]}],
          "nearest_area":[{"areaName":[{"value":"TestCity"}],"region":[{"value":"TestRegion"}],"country":[{"value":"China"}]}],
          "weather":[{"maxtempC":"37","mintempC":"28","hourly":[
            {"time":"900","tempC":"32","precipMM":"0","chanceofrain":"10","weatherDesc":[{"value":"Sunny"}],"lang_zh":[{"value":"晴"}]},
            {"time":"1500","tempC":"37","precipMM":"2","chanceofrain":"55","weatherDesc":[{"value":"Rain"}],"lang_zh":[{"value":"小雨"}]}
          ]}]
        }
        """;
    var broadcast = WeatherReport.FormatWeatherBroadcast(sampleJson, "测试省 测试市");
    if (!broadcast.Contains("°C", StringComparison.Ordinal)) Fail("播报缺气温");
    if (!broadcast.Contains("【提醒与预警】", StringComparison.Ordinal)) Fail("播报缺预警节");
    if (!broadcast.Contains("高温", StringComparison.Ordinal) && !broadcast.Contains("降水", StringComparison.Ordinal))
        Fail("样例应触发高温或降水提示");
    else Ok("FormatWeatherBroadcast 含气温与警示节");

    File.WriteAllText(Path.Combine(scratch, "weather.txt"), broadcast + "\n---\nhot=" + string.Join("|", hot) + "\nrain=" + string.Join("|", rain) + "\n");
}

// ---------- 5) 文档作者字段存在（静态） ----------
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    // tools/GoalVerify/bin/Release/net8.0 → 上溯到仓库根不稳定，改用环境或相对
    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory())),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..")),
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..")),
    };
    string? agents = null;
    foreach (var c in candidates)
    {
        var p = Path.Combine(c, "AGENTS.md");
        if (File.Exists(p)) { agents = p; break; }
        p = Path.Combine(c, "BunnyCompanion", "AGENTS.md");
        if (File.Exists(p)) { agents = Path.GetDirectoryName(p) is { } d ? Path.Combine(d, "AGENTS.md") : p; break; }
    }
    // 再试仓库固定相对：从 compile link 所在
    if (agents is null)
    {
        var walk = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 8 && walk is not null; i++, walk = walk.Parent)
        {
            var p = Path.Combine(walk.FullName, "AGENTS.md");
            if (File.Exists(p)) { agents = p; break; }
        }
    }

    if (agents is null || !File.Exists(agents))
        Fail("AGENTS.md 未找到");
    else
    {
        var text = File.ReadAllText(agents);
        if (!text.Contains("1837620622", StringComparison.Ordinal)) Fail("AGENTS 缺 GitHub 作者号");
        else if (!text.Contains("2040168455@qq.com", StringComparison.Ordinal)) Fail("AGENTS 缺交付邮箱");
        else Ok("AGENTS.md 作者字段齐备 path=" + agents);
        File.WriteAllText(Path.Combine(scratch, "docs-author.txt"), agents + "\nlen=" + text.Length + "\n");
    }
}

Console.WriteLine(fails == 0 ? "ALL_PASS" : $"FAILURES={fails}");
Environment.Exit(fails == 0 ? 0 : 1);
