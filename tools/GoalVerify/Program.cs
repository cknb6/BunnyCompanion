using BunnyCompanion.Engine;
using BunnyCompanion.Models;
using BunnyCompanion.Services;

// 直接驱动产品源码执行门禁自检（非 mock 再实现）
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

    // 农历 2002正月20
    if (!ZodiacService.TryParseLunarChinese("2002正月20", out var lunarSolar))
        Fail("农历 2002正月20 解析失败");
    else
    {
        Ok($"农历2002正月20 → 公历 {lunarSolar}");
        var z2 = ZodiacService.Analyze("2002正月20", "宝宝");
        if (z2.Contains("没识别", StringComparison.Ordinal)) Fail("农历星座分析失败: " + z2);
        else Ok("农历生日星座分析 OK");
    }

    var card = DailyCompanion.BuildDailyCard("宝宝");
    if (!card.Contains("今日陪伴卡", StringComparison.Ordinal)) Fail("陪伴卡格式不对");
    else Ok("DailyCompanion 卡片 OK");

    File.WriteAllText(Path.Combine(scratch, "zodiac-daily.txt"), z + "\n---\n" + card + "\n");
}

// ---------- 2b2) 路径别名（桌面/Desktop/空串） ----------
{
    var deskZh = FolderPathResolver.ResolveAlias("桌面");
    var deskEn = FolderPathResolver.ResolveAlias("Desktop");
    var deskExpand = FolderPathResolver.Expand("桌面");
    var emptyExpand = FolderPathResolver.Expand("");
    var expected = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    if (string.IsNullOrWhiteSpace(deskZh) || !Directory.Exists(deskZh))
        Fail("ResolveAlias 桌面 失败: " + deskZh);
    else
        Ok("ResolveAlias 桌面 OK → " + deskZh);

    if (string.IsNullOrWhiteSpace(deskEn) || !Directory.Exists(deskEn))
        Fail("ResolveAlias Desktop 失败: " + deskEn);
    else
        Ok("ResolveAlias Desktop OK");

    if (!string.Equals(deskExpand, expected, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(deskZh, deskExpand, StringComparison.OrdinalIgnoreCase))
        Fail($"Expand 桌面 不一致 expand={deskExpand} expected={expected}");
    else
        Ok("Expand 桌面 OK");

    if (string.IsNullOrWhiteSpace(emptyExpand) || !Directory.Exists(emptyExpand))
        Fail("Expand 空串 应回落到桌面: " + emptyExpand);
    else
        Ok("Expand 空串→桌面 OK");

    // 前缀：桌面下虚构相对路径应落在桌面目录内
    var child = FolderPathResolver.Expand("桌面" + Path.DirectorySeparatorChar + "goal_verify_probe.txt");
    if (!child.StartsWith(expected.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        Fail("Expand 桌面/子路径 未落在桌面下: " + child);
    else
        Ok("Expand 桌面/子路径 OK");

    File.WriteAllText(Path.Combine(scratch, "folder-alias.txt"),
        $"zh={deskZh}\nen={deskEn}\nexpand={deskExpand}\nempty={emptyExpand}\nchild={child}\n");
}

// ---------- 2c) 系统触发器配置归一化 / 返回检测 ----------
{
    var cfg = new SystemTriggerConfig
    {
        HighCpuThreshold = double.NaN,
        HighMemoryThreshold = 180,
        LowBatteryThreshold = -5,
        IdleTooLongSeconds = -1,
        CooldownSeconds = 0,
    };
    cfg.Normalize();
    if (cfg.HighCpuThreshold != 85 || cfg.HighMemoryThreshold != 100
        || cfg.LowBatteryThreshold != 0 || cfg.IdleTooLongSeconds != 0
        || cfg.CooldownSeconds != 60)
        Fail("SystemTriggerConfig.Normalize 越界修复失败");
    else
        Ok("SystemTriggerConfig.Normalize OK");

    if (!SystemTriggerConfig.HasReturnedFromIdle(true, 3, 600)
        || !SystemTriggerConfig.HasReturnedFromIdle(true, 120, 600)
        || SystemTriggerConfig.HasReturnedFromIdle(false, 3, 600)
        || SystemTriggerConfig.HasReturnedFromIdle(true, 600, 600))
        Fail("久离返回检测失败");
    else
        Ok("久离提醒仅在返回后触发");
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

    // walk 素材默认朝左：向右走 ScaleX=-1
    if (Math.Abs(PetFacing.ScaleXForMove(1) - (-1.0)) > 0.001)
        Fail("向右走 Facing 应为 -1，实际 " + PetFacing.ScaleXForMove(1));
    else
        Ok("PetFacing 向右 ScaleX=-1");
    if (Math.Abs(PetFacing.ScaleXForMove(-1) - 1.0) > 0.001)
        Fail("向左走 Facing 应为 +1");
    else
        Ok("PetFacing 向左 ScaleX=+1");

    File.WriteAllText(Path.Combine(scratch, "click-drag.txt"),
        $"zones={zones.Length}\nclick={click.ActionKey}/{click.Message}\nfling={fling.ActionKey}\nsoft={soft.ActionKey}\ndir={dir}\nintensity={intensity}\n");
}

// ---------- 3b) 自动更新：版本解析 + SHA256 解析 + URL 白名单 ----------
{
    if (!AppUpdateService.TryParseVersion("v1.4.0.27", out var remote)
        || remote.Major != 1 || remote.Minor != 4 || remote.Build != 0 || remote.Revision != 27)
        Fail("TryParseVersion v1.4.0.27 失败: " + remote);
    else
        Ok("TryParseVersion v1.4.0.27 OK");

    if (!AppUpdateService.TryParseVersion("1.3.0", out var baseV) || baseV.Revision != 0)
        Fail("TryParseVersion 1.3.0 失败");
    else
        Ok("TryParseVersion 1.3.0 OK");

    if (!(remote > baseV))
        Fail("版本比较 1.4.0.27 应大于 1.3.0");
    else
        Ok("版本比较 OK");

    var sample = """
        小申陪伴 1.4.0.27

        [win-x64]
        文件（英文名）：BunnyCompanion-win-x64.exe
        SHA256：AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        [win-arm64]
        文件（英文名）：BunnyCompanion-win-arm64.exe
        SHA256：BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB
        """;
    // 上面是 64 个 A/B；重新生成合法 64 hex
    var ha = new string('a', 64);
    var hb = new string('b', 64);
    sample = $"""
        [win-x64]
        文件（英文名）：BunnyCompanion-win-x64.exe
        SHA256：{ha}
        [win-arm64]
        BunnyCompanion-win-arm64.exe  SHA256={hb}
        """;
    var px = AppUpdateService.ParseSha256FromChecksums(sample, "BunnyCompanion-win-x64.exe");
    var pa = AppUpdateService.ParseSha256FromChecksums(sample, "BunnyCompanion-win-arm64.exe");
    if (px != ha) Fail("解析 x64 哈希失败: " + px);
    else Ok("ParseSha256 x64 OK");
    if (pa != hb) Fail("解析 arm64 哈希失败: " + pa);
    else Ok("ParseSha256 arm64 OK");

    if (!AppUpdateService.IsAllowedDownloadUrl(
            "https://github.com/cknb6/BunnyCompanion/releases/download/v1.4.0.1/BunnyCompanion-win-x64.exe"))
        Fail("官方下载 URL 应放行");
    else
        Ok("URL 白名单官方 OK");
    if (AppUpdateService.IsAllowedDownloadUrl("http://evil.example/a.exe"))
        Fail("http 非 HTTPS 不应放行");
    else
        Ok("URL 拒绝 http OK");
    if (AppUpdateService.IsAllowedDownloadUrl("https://evil.example/BunnyCompanion-win-x64.exe"))
        Fail("非 GitHub 域名不应放行");
    else
        Ok("URL 拒绝第三方域名 OK");

    // 403/429 中文限流文案（真实 FormatGitHubHttpError，非拷贝）
    var m403 = AppUpdateService.FormatGitHubHttpError(403, "API rate limit exceeded", "120");
    if (m403.Contains("HTTP 403", StringComparison.Ordinal)
        && !m403.Contains("频率限制", StringComparison.Ordinal)
        && !m403.Contains("限流", StringComparison.Ordinal)
        && !m403.Contains("稍后再试", StringComparison.Ordinal))
        Fail("403 文案不得只剩裸 HTTP 403: " + m403);
    if (!m403.Contains("稍后再试", StringComparison.Ordinal)
        && !m403.Contains("频率限制", StringComparison.Ordinal)
        && !m403.Contains("受限", StringComparison.Ordinal))
        Fail("403 应含限流/稍后再试类中文: " + m403);
    if (!m403.Contains("releases", StringComparison.OrdinalIgnoreCase))
        Fail("403 文案应引导 Releases 页");
    else
        Ok("FormatGitHubHttpError 403 OK");

    var m429 = AppUpdateService.FormatGitHubHttpError(429, null, "30");
    if (!m429.Contains("稍后再试", StringComparison.Ordinal) && !m429.Contains("受限", StringComparison.Ordinal))
        Fail("429 文案应提示稍后再试");
    else
        Ok("FormatGitHubHttpError 429 OK");

    if (!AppUpdateService.IsRateLimitOrAbuseStatus(403)
        || !AppUpdateService.IsRateLimitOrAbuseStatus(429)
        || AppUpdateService.IsRateLimitOrAbuseStatus(200))
        Fail("IsRateLimitOrAbuseStatus 逻辑错误");
    else
        Ok("IsRateLimitOrAbuseStatus OK");

    // 缓存命中：force=false + 间隔未过 → 应复用，不依赖网络
    AppUpdateService.ClearCacheForTests();
    var sampleResult = new AppUpdateService.UpdateCheckResult(
        true, false, "已是最新（测试缓存）", new Version(1, 5, 0, 41),
        new Version(1, 5, 0, 41), "v1.5.0.41", null, null, null, null, null);
    var seedUtc = DateTime.UtcNow;
    AppUpdateService.SeedCacheForTests(sampleResult, seedUtc);
    if (!AppUpdateService.ShouldUseCachedResult(
            force: false,
            minInterval: TimeSpan.FromMinutes(45),
            lastCheckUtc: seedUtc,
            nowUtc: seedUtc.AddMinutes(5),
            hasCachedResult: true))
        Fail("5 分钟内 force=false 应命中缓存");
    else
        Ok("ShouldUseCachedResult 间隔内命中 OK");

    if (AppUpdateService.ShouldUseCachedResult(
            force: true,
            minInterval: TimeSpan.FromMinutes(45),
            lastCheckUtc: seedUtc,
            nowUtc: seedUtc.AddMinutes(1),
            hasCachedResult: true))
        Fail("force=true 不应跳过网络");
    else
        Ok("ShouldUseCachedResult force 跳过网络=否 OK");

    // CheckAsync 走真实缓存路径（force:false，不发起「成功网络」）
    var cached = AppUpdateService.CheckAsync(
        minInterval: TimeSpan.FromHours(2),
        force: false).GetAwaiter().GetResult();
    if (!cached.Success || cached.Message is null
        || !cached.Message.Contains("测试缓存", StringComparison.Ordinal))
        Fail("CheckAsync 应返回 Seed 的缓存结果，实际: " + cached.Message);
    else
        Ok("CheckAsync 缓存复用 OK");

    // 限流 403 → WithCacheNote 推进 lastCheckUtc → 间隔内第二次不得再联网
    AppUpdateService.ClearCacheForTests();
    var okSeed = new AppUpdateService.UpdateCheckResult(
        true, false, "已是最新（限流回退基线）", new Version(1, 5, 0, 42),
        new Version(1, 5, 0, 42), "v1.5.0.42", null, null, null, null, null);
    // lastCheck 设为 2 小时前：模拟「成功已超过 45 分钟」，若 force 会联网
    AppUpdateService.SeedCacheForTests(okSeed, DateTime.UtcNow.AddHours(-2));

    AppUpdateService.HttpSendOverrideForTests = (_, _) =>
    {
        var r = new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        r.Content = new System.Net.Http.StringContent("{\"message\":\"API rate limit exceeded\"}");
        r.Headers.TryAddWithoutValidation("Retry-After", "60");
        return Task.FromResult(r);
    };

    var after403 = AppUpdateService.CheckAsync(minInterval: TimeSpan.FromMinutes(45), force: true)
        .GetAwaiter().GetResult();
    var n1 = AppUpdateService.NetworkRequestCountForTests;
    if (n1 != 1)
        Fail("force+403 应恰好联网 1 次，实际=" + n1);
    if (!after403.Success)
        Fail("有 lastSuccess 时 403 应回退成功结果，Success 应为 true");
    if (after403.Message is null || !after403.Message.Contains("联网受限", StringComparison.Ordinal))
        Fail("403 回退文案应含「联网受限」: " + after403.Message);
    var lag = DateTime.UtcNow - AppUpdateService.LastCheckUtcForTests;
    if (lag > TimeSpan.FromMinutes(2))
        Fail("限流回退后 LastCheckUtc 应刷新为近期，lag=" + lag);

    var afterCool = AppUpdateService.CheckAsync(minInterval: TimeSpan.FromMinutes(45), force: false)
        .GetAwaiter().GetResult();
    var n2 = AppUpdateService.NetworkRequestCountForTests;
    if (n2 != 1)
        Fail("限流回退后 45 分钟间隔内 force=false 不得二次联网，count=" + n2);
    if (!afterCool.Success)
        Fail("冷却内第二次应仍返回缓存成功");
    else
        Ok("限流后冷却内第二次 CheckAsync 不联网 OK");

    // 再 force 一次仍会联网（证明钩子可计数），但间隔内 force=false 不增
    _ = AppUpdateService.CheckAsync(minInterval: TimeSpan.FromMinutes(45), force: true)
        .GetAwaiter().GetResult();
    if (AppUpdateService.NetworkRequestCountForTests != 2)
        Fail("再次 force 应再联网一次，count=" + AppUpdateService.NetworkRequestCountForTests);
    _ = AppUpdateService.CheckAsync(minInterval: TimeSpan.FromMinutes(45), force: false)
        .GetAwaiter().GetResult();
    if (AppUpdateService.NetworkRequestCountForTests != 2)
        Fail("force 后 force=false 仍不得额外联网，count=" + AppUpdateService.NetworkRequestCountForTests);
    else
        Ok("限流冷却：force 联网 / 非 force 缓存 计数 OK");

    AppUpdateService.ClearCacheForTests();
}

// ---------- 4) 天气高温/降水 + 经纬度透传 + Open-Meteo JSON ----------
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
          "nearest_area":[{"areaName":[{"value":"TestCity"}],"region":[{"value":"TestRegion"}],"country":[{"value":"China"}],"latitude":"31.230","longitude":"121.474"}],
          "weather":[{"maxtempC":"37","mintempC":"28","hourly":[
            {"time":"900","tempC":"32","precipMM":"0","chanceofrain":"10","weatherDesc":[{"value":"Sunny"}],"lang_zh":[{"value":"晴"}]},
            {"time":"1500","tempC":"37","precipMM":"2","chanceofrain":"55","weatherDesc":[{"value":"Rain"}],"lang_zh":[{"value":"小雨"}]}
          ]}]
        }
        """;
    var broadcast = WeatherReport.FormatWeatherBroadcast(sampleJson, "测试省 测试市", 31.23, 121.47, "单元测试传入坐标");
    if (!broadcast.Contains("°C", StringComparison.Ordinal)) Fail("播报缺气温");
    if (!broadcast.Contains("【提醒与预警】", StringComparison.Ordinal)) Fail("播报缺预警节");
    if (!broadcast.Contains("【关心提醒】", StringComparison.Ordinal)) Fail("播报缺关心提醒节");
    if (!broadcast.Contains("纬度(Latitude)", StringComparison.Ordinal)) Fail("播报缺纬度字段");
    if (!broadcast.Contains("经度(Longitude)", StringComparison.Ordinal)) Fail("播报缺经度字段");
    if (!broadcast.Contains("31.23", StringComparison.Ordinal)) Fail("播报应含传入纬度 31.23");
    if (!broadcast.Contains("121.47", StringComparison.Ordinal)) Fail("播报应含传入经度 121.47");
    if (!broadcast.Contains("高温", StringComparison.Ordinal) && !broadcast.Contains("降水", StringComparison.Ordinal))
        Fail("样例应触发高温或降水提示");
    else Ok("FormatWeatherBroadcast 含气温/经纬度/警示/关心");

    // Open-Meteo 样例：数字型字段 + 根级 lat/lon
    var omJson = """
        {
          "latitude": 31.247803,
          "longitude": 121.5,
          "elevation": 3.0,
          "timezone": "Asia/Shanghai",
          "current": {
            "temperature_2m": 39.6,
            "relative_humidity_2m": 37,
            "apparent_temperature": 44.6,
            "precipitation": 0.0,
            "weather_code": 2,
            "wind_speed_10m": 11.3
          },
          "hourly": {
            "time": ["2026-07-16T09:00", "2026-07-16T12:00", "2026-07-16T15:00", "2026-07-16T18:00"],
            "temperature_2m": [36.5, 39.1, 39.2, 36.7],
            "precipitation_probability": [5, 29, 48, 41],
            "weather_code": [2, 2, 51, 2],
            "uv_index": [4.55, 8.30, 3.45, 0.85]
          },
          "daily": {
            "time": ["2026-07-16"],
            "temperature_2m_max": [39.5],
            "temperature_2m_min": [31.2],
            "precipitation_sum": [2.6],
            "precipitation_probability_max": [49],
            "uv_index_max": [8.3]
          }
        }
        """;
    var omSnap = WeatherReport.ParseOpenMeteo(omJson, "中国 上海市 上海", 31.23, 121.47, "测试地理编码");
    if (omSnap.Latitude is null || omSnap.Longitude is null) Fail("Open-Meteo 快照应有经纬度");
    if (Math.Abs(omSnap.TempC - 39.6) > 0.01) Fail("Open-Meteo 气温解析错误");
    if (omSnap.UvIndexMax is null || omSnap.UvIndexMax < 8) Fail("Open-Meteo 应解析到 UV≥8");
    var omText = WeatherReport.FormatSnapshot(omSnap);
    if (!omText.Contains("纬度(Latitude)", StringComparison.Ordinal)) Fail("Open-Meteo 播报缺纬度");
    if (!omText.Contains("经度(Longitude)", StringComparison.Ordinal)) Fail("Open-Meteo 播报缺经度");
    if (!omText.Contains("【关心提醒】", StringComparison.Ordinal)) Fail("Open-Meteo 播报缺关心提醒");
    if (!omText.Contains("防晒", StringComparison.Ordinal) && !omText.Contains("高温", StringComparison.Ordinal))
        Fail("高温+高 UV 应有关心/预警");
    else Ok("ParseOpenMeteo + FormatSnapshot 经纬度与关心提醒 OK");

    var cares = WeatherReport.BuildCareTips(omSnap);
    if (cares.Count == 0) Fail("BuildCareTips 不应为空");
    else Ok("BuildCareTips 条数=" + cares.Count);

    File.WriteAllText(Path.Combine(scratch, "weather.txt"),
        broadcast + "\n---\n" + omText + "\n---\nhot=" + string.Join("|", hot) +
        "\nrain=" + string.Join("|", rain) + "\ncares=" + string.Join("|", cares) + "\n");
}

// ---------- 5) 办公计划 OfficePlanStore + AgentMode ----------
{
    var planPath = Path.Combine(scratch, "office_plan_test.json");
    var store = new OfficePlanStore(planPath);
    var set = store.SetPlan("整理PDF", "搜索桌面pdf\n预览移动\n执行移动\n回报路径");
    if (!set.Contains("办公计划", StringComparison.Ordinal)) Fail("plan_set 应输出计划标题");
    else Ok("plan_set OK");

    var steps = OfficePlanStore.ParseSteps("1. a\n2. b\n| c");
    if (steps.Count != 3) Fail("ParseSteps 应解析 3 步，实际=" + steps.Count);
    else Ok("ParseSteps count=3");

    var jsonSteps = OfficePlanStore.ParseSteps("""["读文件","改文件","保存"]""");
    if (jsonSteps.Count != 3) Fail("JSON 步骤解析失败");
    else Ok("ParseSteps JSON OK");

    store.Tick(1, "done", "found 3");
    store.Tick(2, "skip", null);
    var st = store.StatusText();
    if (!st.Contains("[x]", StringComparison.Ordinal) || !st.Contains("[-]", StringComparison.Ordinal))
        Fail("plan_tick 状态标记缺失");
    else Ok("plan_tick 状态 OK");

    var badTick = store.Tick(3, "finished_typo", null);
    if (!badTick.Contains("未知 status", StringComparison.Ordinal))
        Fail("未知 status 应拒绝而非默认完成");
    else Ok("未知 status 拒绝 OK");

    if (!OfficePlanStore.TryParseStatus("done", out var ps) || ps != OfficePlanStepStatus.Done)
        Fail("TryParseStatus done");
    if (OfficePlanStore.TryParseStatus("not-a-status", out _))
        Fail("未知 status TryParse 应 false");
    else Ok("TryParseStatus 边界 OK");

    var cleared = store.ClearPlan("测试清空");
    if (store.HasPlan || !cleared.Contains("清空", StringComparison.Ordinal))
        Fail("ClearPlan 应清空");
    else Ok("plan_clear / ClearPlan OK");

    // 再设计划测落盘
    store.SetPlan("持久化测", "步骤A\n步骤B");
    // 落盘再读
    var store2 = new OfficePlanStore(planPath);
    if (!store2.HasPlan) Fail("计划应持久化");
    else Ok("office_plan 持久化 OK");

    var settings = new PetSettings { AgentMode = "office" };
    settings.Normalize();
    if (!settings.IsOfficeMode) Fail("AgentMode=office 应识别为办公");
    settings.AgentMode = "companion";
    settings.Normalize();
    if (settings.IsOfficeMode) Fail("companion 不应为办公");
    else Ok("PetSettings.AgentMode 归一 OK");

    // 源码结构：办公轮数配置应存在于 AiConfig（静态检查）
    var aiConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "BunnyCompanion", "Services", "AiConfig.cs"));
    // GoalVerify 输出在 tools/GoalVerify/bin/Release/net8.0/ → 上溯到仓库根
    var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    if (!Directory.Exists(Path.Combine(repoRoot, "BunnyCompanion")))
        repoRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", ".."));
    if (!Directory.Exists(Path.Combine(repoRoot, "BunnyCompanion")))
        repoRoot = Directory.GetCurrentDirectory();
    aiConfigPath = Path.Combine(repoRoot, "BunnyCompanion", "Services", "AiConfig.cs");
    if (!File.Exists(aiConfigPath))
        Fail("找不到 AiConfig.cs: " + aiConfigPath);
    else
    {
        var cfg = File.ReadAllText(aiConfigPath);
        if (!cfg.Contains("MaxToolRoundsOffice", StringComparison.Ordinal))
            Fail("AiConfig 应定义 MaxToolRoundsOffice");
        if (!cfg.Contains("StepOfficeMaxTokens", StringComparison.Ordinal))
            Fail("AiConfig 应定义 StepOfficeMaxTokens");
        else
            Ok("AiConfig 办公预算常量存在");
    }

    // 工具定义源码应含 plan_set / batch_move / web_search_results
    var toolkitPath = Path.Combine(repoRoot, "BunnyCompanion", "Services", "WindowsAgentToolkit.cs");
    if (!File.Exists(toolkitPath))
        Fail("找不到 WindowsAgentToolkit.cs");
    else
    {
        var tk = File.ReadAllText(toolkitPath);
        foreach (var name in new[] { "plan_set", "plan_tick", "plan_clear", "batch_move", "batch_rename", "web_search_results", "confirm" })
        {
            if (!tk.Contains("\"" + name + "\"", StringComparison.Ordinal)
                && !tk.Contains(name, StringComparison.Ordinal))
                Fail("工具箱缺少 " + name);
        }
        if (!tk.Contains("AllowBatchExecute", StringComparison.Ordinal)
            && !tk.Contains("BatchPreviewGate", StringComparison.Ordinal))
            Fail("batch 执行门闩缺失");
        else
            Ok("办公工具定义 + batch 门闩存在");
    }

    var promptPath = Path.Combine(repoRoot, "BunnyCompanion", "Services", "AgentSystemPrompt.cs");
    if (!File.Exists(promptPath) || !File.ReadAllText(promptPath).Contains("BuildOffice", StringComparison.Ordinal))
        Fail("AgentSystemPrompt 应有 BuildOffice");
    else
        Ok("AgentSystemPrompt.BuildOffice 存在");

    // 办公循环不得「工具跑了却整链失败」：源码须含累计工具结果 + 收尾兜底
    var agentPath = Path.Combine(repoRoot, "BunnyCompanion", "Services", "AiAgentService.cs");
    var agentSrc = File.Exists(agentPath) ? File.ReadAllText(agentPath) : "";
    if (!agentSrc.Contains("accumulatedToolResults", StringComparison.Ordinal)
        || !agentSrc.Contains("办公·工具兜底", StringComparison.Ordinal)
        || !agentSrc.Contains("OfficeEmptyAfterToolsRetries", StringComparison.Ordinal))
        Fail("AiAgentService 办公兜底路径缺失");
    else
        Ok("办公 Agent 工具累计+兜底路径存在");

    var cfgPath2 = Path.Combine(repoRoot, "BunnyCompanion", "Services", "AiConfig.cs");
    var cfg2 = File.Exists(cfgPath2) ? File.ReadAllText(cfgPath2) : "";
    if (!cfg2.Contains("OfficeEmptyAfterToolsRetries", StringComparison.Ordinal)
        || !cfg2.Contains("StepOfficeEffort = \"medium\"", StringComparison.Ordinal))
        Fail("AiConfig 办公 medium effort / 空 content 重试配置缺失");
    else
        Ok("办公配置 medium effort + 空 content 重试 OK");

    File.WriteAllText(Path.Combine(scratch, "office_plan.txt"), st + "\n---\n" + set + "\n");
}

Console.WriteLine(fails == 0 ? "ALL_PASS" : $"FAILURES={fails}");
Environment.Exit(fails == 0 ? 0 : 1);
