using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 本机 Windows Agent 工具箱：文件/命令/定位/天气/系统信息等。
/// 在用户机器上执行，权限随进程（管理员运行时可操作更多路径）。
/// </summary>
public static class WindowsAgentToolkit
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(25),
    };

    static WindowsAgentToolkit()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion-Agent/1.4");
        try
        {
            // 国内定位接口（太平洋电脑网）返回 GBK
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // ignore
        }
    }

    public static bool IsRunningAsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>OpenAI / Step / OpenRouter 兼容的 tools 定义。</summary>
    public static JsonArray BuildToolDefinitions() =>
    [
        Tool("get_system_info", "获取本机系统信息：计算机名、用户、OS、是否管理员、CPU/内存概况、时间时区。", new JsonObject()),
        Tool("get_location",
            "推断当前位置（优先国内 IP 定位接口，降低 VPN/境外出口误报）。返回城市/地区/运营商/IP 与是否可能 VPN。" +
            "用户问「我在哪」「定位」时必须调用。",
            new JsonObject()),
        Tool("get_weather",
            "查询实时天气与高温/降水等预警。可指定 city；不指定则优先用国内 IP 定位城市再查天气。",
            Props(("city", "string", "城市名，如 北京、上海、深圳；可空则自动定位（偏中国网络）"))),
        Tool("memo_add",
            "添加备忘/提醒。text 为事项；due 可选本地时间（如 2026-07-16 15:30 或 30分钟后/明天9点 口语会由上层解析，工具侧优先 ISO）。",
            Props(
                ("text", "string", "提醒内容"),
                ("due", "string", "可选到期时间描述或 yyyy-MM-dd HH:mm")),
            required: ["text"]),
        Tool("memo_list",
            "列出未完成备忘提醒。",
            Props(("include_done", "boolean", "是否包含已完成，默认 false"))),
        Tool("memo_done",
            "将备忘标记完成。id 可为完整 id 或前缀，也可为正文关键词。",
            Props(("id", "string", "备忘 id 前缀或正文关键词")),
            required: ["id"]),
        Tool("memory_list",
            "查看长期记忆摘要（人物、偏好、星座、备忘）。",
            new JsonObject()),
        Tool("agent_md_read",
            "读取本地 agent.md 长期记忆文件内容（自动摘要压缩后的 Markdown）。",
            Props(("max_chars", "integer", "最多返回字符，默认 8000"))),
        Tool("agent_md_path",
            "返回本机 agent.md 的完整路径，便于用户打开编辑手写备注区。",
            new JsonObject()),
        Tool("zodiac_analyze",
            "星座趣味分析。传入生日（公历1999-8-15 / 农历2002正月20）或星座名（处女座）。也可用 sign/date 参数。",
            Props(
                ("query", "string", "生日或星座名，优先"),
                ("sign", "string", "可选星座名"),
                ("date", "string", "可选生日"))),
        Tool("daily_card",
            "生成今日陪伴卡：能量/穿搭/习惯/一句话（趣味）。",
            Props(("name", "string", "可选称呼"))),
        Tool("list_dir",
            "列出目录内容（文件与子文件夹）。path 可用绝对路径，也可用别名：桌面/Desktop、文档、下载、图片等。",
            Props(
                ("path", "string", "目录路径或别名，如 桌面、Desktop、下载、C:\\Users\\Name\\Documents；空则默认桌面"),
                ("max_entries", "integer", "最多返回条数，默认 80")),
            required: ["path"]),
        Tool("read_file",
            "读取文本文件内容（自动限长）。用于查看代码、日志、配置、文档。",
            Props(
                ("path", "string", "文件绝对路径"),
                ("max_chars", "integer", "最大字符数，默认 20000")),
            required: ["path"]),
        Tool("write_file",
            "创建或覆盖写入文本文件。可新建文档、改配置、写代码。",
            Props(
                ("path", "string", "目标文件绝对路径"),
                ("content", "string", "完整写入内容"),
                ("create_dirs", "boolean", "是否自动创建父目录，默认 true")),
            required: ["path", "content"]),
        Tool("append_file",
            "在文本文件末尾追加内容。",
            Props(
                ("path", "string", "文件路径"),
                ("content", "string", "追加内容")),
            required: ["path", "content"]),
        Tool("move_path",
            "移动或重命名文件/文件夹。",
            Props(
                ("source", "string", "源路径"),
                ("destination", "string", "目标路径")),
            required: ["source", "destination"]),
        Tool("copy_path",
            "复制文件或文件夹。",
            Props(
                ("source", "string", "源路径"),
                ("destination", "string", "目标路径")),
            required: ["source", "destination"]),
        Tool("delete_path",
            "删除文件或空目录。对非空目录需 recursive=true。高危操作，仅在用户明确要求时使用。",
            Props(
                ("path", "string", "路径"),
                ("recursive", "boolean", "目录是否递归删除，默认 false")),
            required: ["path"]),
        Tool("search_files",
            "在目录中按文件名通配搜索（如 *.pdf、*报告*）。",
            Props(
                ("root", "string", "搜索根目录"),
                ("pattern", "string", "通配符，如 *.docx"),
                ("max_results", "integer", "最多结果数，默认 40")),
            required: ["root", "pattern"]),
        Tool("run_command",
            "在本机执行 PowerShell 命令并返回 stdout/stderr。可用于高级操作、批量处理、查询进程等。注意安全。",
            Props(
                ("command", "string", "PowerShell 命令文本"),
                ("timeout_seconds", "integer", "超时秒数，默认 45")),
            required: ["command"]),
        Tool("open_path",
            "用系统默认程序打开文件/文件夹/URL。",
            Props(("path", "string", "路径或 https URL")),
            required: ["path"]),
        Tool("get_clipboard", "读取当前剪贴板文本。", new JsonObject()),
        Tool("set_clipboard",
            "写入剪贴板文本。",
            Props(("text", "string", "要写入的文本")),
            required: ["text"]),
        Tool("get_special_folder",
            "解析 Windows 特殊文件夹路径：Desktop/Documents/Downloads/Pictures/Music/Videos/UserProfile/AppData。",
            Props(("name", "string", "文件夹名关键词")),
            required: ["name"]),
        Tool("create_directory",
            "创建目录（可递归）。",
            Props(("path", "string", "目录路径")),
            required: ["path"]),
        Tool("get_process_list",
            "列出当前运行的主要进程（名称、PID、内存）。",
            Props(("filter", "string", "可选：进程名包含的关键字"), ("max", "integer", "最多条数默认 30"))),
        Tool("get_system_monitor",
            "获取系统监控快照：CPU 占用、内存使用、电池、闲置时长、处理器数。用户问电脑卡不卡/电量/内存/久坐时调用。",
            new JsonObject()),
        Tool("fetch_url",
            "抓取指定网址的网页正文（去标签，限长返回）。用于读文章、查资料、总结网页。",
            Props(
                ("url", "string", "网址，可带或不带 http(s)://"),
                ("max_chars", "integer", "最多返回字符，默认 6000")),
            required: ["url"]),
        Tool("web_search",
            "用系统默认浏览器打开搜索结果页（bing/google/baidu）。",
            Props(
                ("query", "string", "搜索词"),
                ("engine", "string", "搜索引擎：bing(默认)/google/baidu")),
            required: ["query"]),
        Tool("read_browser_tab",
            "读取前台浏览器（Chrome/Edge 等）当前标签页的 URL 与标题。用户说“总结这个网页/这个页面”时先调用。",
            new JsonObject()),
        Tool("open_url",
            "用系统默认浏览器打开指定网址。",
            Props(("url", "string", "网址")),
            required: ["url"]),
        Tool("skill_list",
            "列出本地技能插件（Markdown 技能目录）。用户问“有哪些技能/能做什么”时调用。",
            new JsonObject()),
        Tool("skill_get",
            "读取指定技能的正文（指令模板）。",
            Props(("name", "string", "技能名或文件名")),
            required: ["name"]),
        Tool("skill_run",
            "执行技能里定义的命令（PowerShell 或可执行）。仅执行用户明确要求的技能。",
            Props(
                ("name", "string", "技能名或文件名"),
                ("arguments", "string", "可选附加参数"),
                ("timeout_seconds", "integer", "超时秒数，默认 30")),
            required: ["name"]),
        Tool("notify_user",
            "向对话返回一条给用户看的中间状态说明（不会弹系统通知）。用于说明正在执行的步骤。",
            Props(("message", "string", "说明文字")),
            required: ["message"]),
        Tool("speak_text",
            "用语音朗读一段文字给用户听（在线 TTS 优先，失败回退系统语音）。用户说「读给我听」「念出来」「用语音说」时调用。",
            Props(("text", "string", "要朗读的中文内容，建议不超过 300 字")),
            required: ["text"]),
        Tool("stop_speak",
            "停止当前语音朗读。",
            new JsonObject()),
    ];

    public static async Task<string> ExecuteAsync(string name, JsonObject? args, CancellationToken ct)
    {
        args ??= new JsonObject();
        try
        {
            return name switch
            {
                "get_system_info" => GetSystemInfo(),
                "get_location" => await GetLocationAsync(ct).ConfigureAwait(false),
                "get_weather" => await GetWeatherAsync(Str(args, "city"), ct).ConfigureAwait(false),
                "memo_add" => MemoAdd(Str(args, "text"), Str(args, "due")),
                "memo_list" => CompanionRuntime.Memory.ListMemosText(Bool(args, "include_done", false)),
                "memo_done" => CompanionRuntime.Memory.CompleteMemo(Str(args, "id"))
                    ? "已勾掉这条备忘～"
                    : "没找到对应备忘，可用 memo_list 看列表。",
                "memory_list" => MemoryListText(),
                "agent_md_read" => AgentMdRead(Int(args, "max_chars", 8000)),
                "agent_md_path" => CompanionRuntime.AgentMd.FilePath,
                "zodiac_analyze" => ZodiacAnalyze(args),
                "daily_card" => DailyCompanion.BuildDailyCard(
                    string.IsNullOrWhiteSpace(Str(args, "name")) ? "宝宝" : Str(args, "name")),
                "list_dir" => ListDir(Str(args, "path"), Int(args, "max_entries", 80)),
                "read_file" => ReadFile(Str(args, "path"), Int(args, "max_chars", 20000)),
                "write_file" => WriteFile(Str(args, "path"), Str(args, "content"), Bool(args, "create_dirs", true)),
                "append_file" => AppendFile(Str(args, "path"), Str(args, "content")),
                "move_path" => MovePath(Str(args, "source"), Str(args, "destination")),
                "copy_path" => CopyPath(Str(args, "source"), Str(args, "destination")),
                "delete_path" => DeletePath(Str(args, "path"), Bool(args, "recursive", false)),
                "search_files" => SearchFiles(Str(args, "root"), Str(args, "pattern"), Int(args, "max_results", 40)),
                "run_command" => await RunCommandAsync(Str(args, "command"), Int(args, "timeout_seconds", 45), ct)
                    .ConfigureAwait(false),
                "open_path" => OpenPath(Str(args, "path")),
                "get_clipboard" => GetClipboard(),
                "set_clipboard" => SetClipboard(Str(args, "text")),
                "get_special_folder" => GetSpecialFolder(Str(args, "name")),
                "create_directory" => CreateDirectory(Str(args, "path")),
                "get_process_list" => GetProcessList(Str(args, "filter"), Int(args, "max", 30)),
                "get_system_monitor" => SystemMonitorService.BuildSnapshot(),
                "fetch_url" => await BrowserService.FetchUrlAsync(Str(args, "url"), Int(args, "max_chars", 6000), ct)
                    .ConfigureAwait(false),
                "web_search" => BrowserService.OpenSearch(Str(args, "query"),
                    string.IsNullOrWhiteSpace(Str(args, "engine")) ? "bing" : Str(args, "engine")),
                "read_browser_tab" => BrowserService.ReadActiveBrowserTab(),
                "open_url" => BrowserService.OpenUrl(Str(args, "url")),
                "skill_list" => CompanionRuntime.Skills.ListText(),
                "skill_get" => CompanionRuntime.Skills.GetBody(Str(args, "name")) ?? "未找到该技能",
                "skill_run" => await CompanionRuntime.Skills.RunCommandAsync(
                        Str(args, "name"), Str(args, "arguments"), Int(args, "timeout_seconds", 30), ct)
                    .ConfigureAwait(false),
                "notify_user" => "OK: " + Str(args, "message"),
                "speak_text" => SpeakTextTool(Str(args, "text")),
                "stop_speak" => StopSpeakTool(),
                _ => $"错误：未知工具 {name}",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"错误：{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string ZodiacAnalyze(JsonObject args)
    {
        var query = Str(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            query = Str(args, "date");
        if (string.IsNullOrWhiteSpace(query))
            query = Str(args, "sign");
        // 两者都有时拼在一起让解析器优先吃日期
        var sign = Str(args, "sign");
        var date = Str(args, "date");
        if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(sign))
            query = date + " " + sign;
        return ZodiacService.Analyze(query, "宝宝");
    }

    private static string SpeakTextTool(string text)
    {
        text = (text ?? "").Trim();
        if (string.IsNullOrEmpty(text))
            return "错误：没有可朗读的文字。";
        if (text.Length > 500)
            text = text[..500];

        try
        {
            // 用户通过对话明确要求朗读时总是执行（与「自动朗读回复」开关无关）
            VoiceService.Speak(text);
            return "已开始朗读。";
        }
        catch (Exception ex)
        {
            return "朗读失败：" + ex.Message;
        }
    }

    private static string StopSpeakTool()
    {
        try
        {
            VoiceService.Stop();
            return "已停止朗读。";
        }
        catch (Exception ex)
        {
            return "停止朗读失败：" + ex.Message;
        }
    }

    private static string MemoryListText()
    {
        var block = CompanionRuntime.Memory.FormatForSystemPrompt();
        var md = CompanionRuntime.AgentMd.FormatForSystemPrompt(4000);
        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(block)
            ? "（结构化记忆尚空）"
            : block);
        sb.AppendLine();
        sb.AppendLine("--- agent.md ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(md) ? "（agent.md 尚无摘要）" : md);
        sb.AppendLine();
        sb.AppendLine("文件: " + CompanionRuntime.AgentMd.FilePath);
        return sb.ToString().Trim();
    }

    private static string AgentMdRead(int maxChars)
    {
        maxChars = Math.Clamp(maxChars, 500, 50_000);
        var raw = CompanionRuntime.AgentMd.ReadRaw();
        if (string.IsNullOrWhiteSpace(raw))
            return "（agent.md 为空）路径: " + CompanionRuntime.AgentMd.FilePath;
        if (raw.Length > maxChars)
            raw = raw[..maxChars] + "\n…（已截断）";
        return raw + "\n\n路径: " + CompanionRuntime.AgentMd.FilePath;
    }

    private static string MemoAdd(string text, string dueRaw)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "错误：提醒内容不能为空";
        DateTime? due = null;
        if (!string.IsNullOrWhiteSpace(dueRaw))
        {
            if (DateTime.TryParse(dueRaw, out var dt))
                due = dt;
            else if (CompanionMemoryService.TryParseMemo("提醒我" + dueRaw + text, out var body, out var d))
            {
                due = d;
                if (!string.IsNullOrWhiteSpace(body) && body != "你设的提醒")
                    text = body;
            }
        }
        else if (CompanionMemoryService.TryParseMemo(text, out var body2, out var d2))
        {
            text = body2;
            due = d2;
        }

        var item = CompanionRuntime.Memory.AddMemo(text, due, "tool");
        return due is null
            ? $"已记下待办：{item.Text}"
            : $"好，{due:MM-dd HH:mm} 我会提醒你：{item.Text}";
    }

    // ---------- implementations ----------

    private static string GetSystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"计算机名: {Environment.MachineName}");
        sb.AppendLine($"用户: {Environment.UserDomainName}\\{Environment.UserName}");
        sb.AppendLine($"操作系统: {Environment.OSVersion}");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"管理员权限: {(IsRunningAsAdmin() ? "是" : "否（部分系统目录可能拒绝访问）")}");
        sb.AppendLine($"64位进程: {Environment.Is64BitProcess}");
        sb.AppendLine($"处理器数: {Environment.ProcessorCount}");
        sb.AppendLine($"工作集: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB");
        sb.AppendLine($"本机时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"时区: {TimeZoneInfo.Local.DisplayName}");
        sb.AppendLine($"用户目录: {Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)}");
        sb.AppendLine($"桌面: {Environment.GetFolderPath(Environment.SpecialFolder.Desktop)}");
        sb.AppendLine($"文档: {Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}");
        sb.AppendLine($"下载: {GetDownloadsPath()}");
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady);
            foreach (var d in drives)
                sb.AppendLine($"磁盘 {d.Name} 可用 {d.AvailableFreeSpace / 1024 / 1024 / 1024}GB / 共 {d.TotalSize / 1024 / 1024 / 1024}GB");
        }
        catch { /* ignore */ }

        return sb.ToString().Trim();
    }

    /// <summary>公网定位结果（偏中国网络优先）。</summary>
    private sealed record GeoHit(
        string Source,
        string Ip,
        string Country,
        string Province,
        string City,
        string District,
        string Isp,
        string Lat,
        string Lon,
        bool PreferChinaSource);

    private static async Task<string> GetLocationAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【本机网络】");
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up
                                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                         .Take(6))
            {
                var props = ni.GetIPProperties();
                var ipv4 = props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToList();
                if (ipv4.Count == 0) continue;
                sb.AppendLine($"- {ni.Name}: {string.Join(", ", ipv4)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"本机网卡: {ex.Message}");
        }

        sb.AppendLine();
        sb.AppendLine("【公网定位 · 优先国内接口】");
        var hit = await ResolvePublicGeoPreferChinaAsync(ct).ConfigureAwait(false);
        if (hit is null)
        {
            sb.AppendLine("公网定位暂时失败（网络或接口限制）。仍可根据本机网卡与公司网络环境推断。");
            sb.AppendLine("提示: 若开了 VPN/代理，出口 IP 会显示境外城市，属正常现象。");
            return sb.ToString().Trim();
        }

        sb.AppendLine($"定位源: {hit.Source}");
        sb.AppendLine($"IP: {hit.Ip}");
        sb.AppendLine($"国家: {hit.Country}");
        sb.AppendLine($"省/州: {hit.Province}");
        sb.AppendLine($"城市: {hit.City}");
        if (!string.IsNullOrWhiteSpace(hit.District))
            sb.AppendLine($"区: {hit.District}");
        if (!string.IsNullOrWhiteSpace(hit.Lat) && !string.IsNullOrWhiteSpace(hit.Lon))
            sb.AppendLine($"经纬度: {hit.Lat}, {hit.Lon}");
        if (!string.IsNullOrWhiteSpace(hit.Isp))
            sb.AppendLine($"运营商/ISP: {hit.Isp}");

        sb.AppendLine();
        sb.AppendLine(BuildGeoDisclaimer(hit));
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 优先国内 IP 库；若多源冲突且存在「中国」结果，优先采用国内源，避免 VPN 境外出口误导。
    /// </summary>
    private static async Task<GeoHit?> ResolvePublicGeoPreferChinaAsync(CancellationToken ct)
    {
        var hits = new List<GeoHit>();

        // 1) 太平洋电脑网 whois（国内，中文省市区，GBK）
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://whois.pconline.com.cn/ipJson.jsp?json=true");
            req.Headers.TryAddWithoutValidation("User-Agent", "XiaoShenCompanion/1.4");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode && bytes.Length > 8)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var body = Encoding.GetEncoding("GBK").GetString(bytes);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var err = G(root, "err");
                if (string.IsNullOrWhiteSpace(err))
                {
                    var city = G(root, "city");
                    var pro = G(root, "pro");
                    hits.Add(new GeoHit(
                        "国内·太平洋电脑网",
                        G(root, "ip"),
                        "中国",
                        pro,
                        string.IsNullOrWhiteSpace(city) ? pro : city,
                        G(root, "region"),
                        G(root, "addr"),
                        "",
                        "",
                        PreferChinaSource: true));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* next */ }

        // 2) 纯文本国内：myip.ipip.net
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://myip.ipip.net");
            req.Headers.TryAddWithoutValidation("User-Agent", "XiaoShenCompanion/1.4");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var text = (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim();
            // 例：当前 IP：1.2.3.4  来自于：中国 北京 北京  移动
            if (resp.IsSuccessStatusCode && text.Contains("IP", StringComparison.OrdinalIgnoreCase))
            {
                var ipM = Regex.Match(text, @"(\d{1,3}(?:\.\d{1,3}){3})");
                var fromM = Regex.Match(text, @"来自于[:：]\s*(.+)");
                var from = fromM.Success ? fromM.Groups[1].Value.Trim() : text;
                var parts = from.Split([' ', '\t', '　'], StringSplitOptions.RemoveEmptyEntries);
                var country = parts.Length > 0 ? parts[0] : "";
                var province = parts.Length > 1 ? parts[1] : "";
                var city = parts.Length > 2 ? parts[2] : province;
                var isp = parts.Length > 3 ? string.Join(" ", parts.Skip(3)) : "";
                hits.Add(new GeoHit(
                    "国内·ipip.net",
                    ipM.Success ? ipM.Groups[1].Value : "",
                    country,
                    province,
                    city,
                    "",
                    isp,
                    "",
                    "",
                    PreferChinaSource: true));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* next */ }

        // 3) ip.sb（国际，作对照；VPN 时往往出境外）
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.ip.sb/geoip");
            req.Headers.TryAddWithoutValidation("User-Agent", "XiaoShenCompanion/1.4");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                hits.Add(new GeoHit(
                    "国际·ip.sb",
                    G(root, "ip"),
                    G(root, "country") is { Length: > 0 } c ? c : G(root, "country_code"),
                    G(root, "region"),
                    G(root, "city"),
                    "",
                    string.IsNullOrWhiteSpace(G(root, "isp")) ? G(root, "organization") : G(root, "isp"),
                    G(root, "latitude"),
                    G(root, "longitude"),
                    PreferChinaSource: false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* next */ }

        // 4) ip-api 中文（国际备用）
        try
        {
            var body = await Http.GetStringAsync(
                "http://ip-api.com/json/?lang=zh-CN&fields=status,message,country,regionName,city,district,lat,lon,isp,org,query,timezone",
                ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (G(root, "status") != "fail")
            {
                hits.Add(new GeoHit(
                    "国际·ip-api",
                    G(root, "query"),
                    G(root, "country"),
                    G(root, "regionName"),
                    G(root, "city"),
                    G(root, "district"),
                    string.IsNullOrWhiteSpace(G(root, "isp")) ? G(root, "org") : G(root, "isp"),
                    G(root, "lat"),
                    G(root, "lon"),
                    PreferChinaSource: false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* next */ }

        if (hits.Count == 0)
            return null;

        // 裁决：有「中国」结果则优先国内源；否则取国内源优先、再国际源
        static bool IsChina(GeoHit h)
        {
            var c = (h.Country ?? "") + h.Province + h.City + h.Isp;
            return c.Contains("中国", StringComparison.Ordinal)
                   || c.Contains("China", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(h.Country, "CN", StringComparison.OrdinalIgnoreCase)
                   || Regex.IsMatch(c, @"北京|上海|广东|浙江|江苏|四川|湖北|湖南|河南|河北|山东|福建|陕西|重庆|天津|深圳|广州|杭州|成都|武汉|南京|西安|苏州|青岛|大连|厦门|香港|台湾|澳门");
        }

        var chinaHits = hits.Where(IsChina).ToList();
        if (chinaHits.Count > 0)
        {
            // 国内源优先
            return chinaHits.FirstOrDefault(h => h.PreferChinaSource) ?? chinaHits[0];
        }

        // 全是境外：仍返回，但调用方会加 VPN 警告
        return hits.FirstOrDefault(h => h.PreferChinaSource) ?? hits[0];
    }

    private static string BuildGeoDisclaimer(GeoHit hit)
    {
        var sb = new StringBuilder();
        sb.AppendLine("说明: 位置由公网出口 IP 近似推断（城市级），不是 GPS。");

        var blob = $"{hit.Country}{hit.Province}{hit.City}{hit.Isp}{hit.Source}";
        var looksAbroad = !(blob.Contains("中国", StringComparison.Ordinal)
                            || blob.Contains("China", StringComparison.OrdinalIgnoreCase)
                            || Regex.IsMatch(blob, @"北京|上海|广东|浙江|江苏|四川|深圳|广州|杭州|成都"));
        var looksVpn = Regex.IsMatch(
            hit.Isp + hit.Source,
            @"VPN|Proxy|Cloudflare|Amazon|AWS|Google|Azure|DigitalOcean|Linode|Hetzner|Akamai|CDN|数据中心|机房",
            RegexOptions.IgnoreCase);

        if (looksAbroad || looksVpn)
        {
            sb.AppendLine("⚠️ 注意: 当前出口 IP 更像境外/云厂商/代理（常见于开了 VPN、公司跨境专线、加速器）。");
            sb.AppendLine("若你实际在国内，请先关闭 VPN/代理后再查「我在哪」；或直接说城市名查天气，例如「上海天气」。");
        }
        else if (!hit.PreferChinaSource)
        {
            sb.AppendLine("本次使用了国际 IP 库；若结果与你所在城市不符，可关闭 VPN 后重试。");
        }
        else
        {
            sb.AppendLine("本次优先使用了国内 IP 定位接口，更贴近中国运营商出口。");
        }

        sb.AppendLine("公司统一出口可能显示总部/机房城市，不等于你人在那栋楼。");
        return sb.ToString().Trim();
    }

    private static async Task<string> GetWeatherAsync(string? city, CancellationToken ct)
    {
        city = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        string? lat = null, lon = null, place = city;

        if (city is null)
        {
            // 与 get_location 同一套「国内优先」规则，避免 VPN 把天气查到境外
            var hit = await ResolvePublicGeoPreferChinaAsync(ct).ConfigureAwait(false);
            if (hit is not null)
            {
                place = string.Join(" ", new[] { hit.Country, hit.Province, hit.City, hit.District }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                lat = hit.Lat;
                lon = hit.Lon;
                city = !string.IsNullOrWhiteSpace(hit.City) ? hit.City : hit.Province;
                // 境外 VPN 结果不要拿去查天气，改为让用户说城市
                var abroad = BuildGeoDisclaimer(hit).Contains("境外", StringComparison.Ordinal)
                             || BuildGeoDisclaimer(hit).Contains("VPN", StringComparison.Ordinal);
                if (abroad && !string.IsNullOrWhiteSpace(hit.Country)
                    && !hit.Country.Contains("中国", StringComparison.Ordinal)
                    && !hit.Country.Contains("China", StringComparison.OrdinalIgnoreCase))
                {
                    return "当前公网出口更像 VPN/境外 IP，自动定位不可靠。\n" +
                           "请直接说城市查天气，例如：「北京天气」「上海今天热不热」。\n\n" +
                           BuildGeoDisclaimer(hit);
                }
            }
        }

        // wttr.in 免费天气，支持中文 + 逐时降水
        try
        {
            var q = !string.IsNullOrWhiteSpace(lat) && !string.IsNullOrWhiteSpace(lon)
                ? $"{lat},{lon}"
                : (city ?? "Beijing");
            // 中文城市名对 wttr 更友好
            if (q.Contains("市", StringComparison.Ordinal) && q.EndsWith("市", StringComparison.Ordinal))
                q = q.TrimEnd('市');
            var url = $"https://wttr.in/{Uri.EscapeDataString(q)}?format=j1&lang=zh";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");
            req.Headers.TryAddWithoutValidation("User-Agent", "XiaoShenCompanion/1.4");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var report = WeatherReport.FormatWeatherBroadcast(body, place);
                if (city is null)
                    report += "\n\n（定位规则: 优先国内 IP 库；开 VPN 时请直接说城市名。）";
                return report;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"天气查询失败: {ex.Message}";
        }

        return "天气查询失败：接口无响应。";
    }

    /// <summary>转发至 WeatherReport，保持工具入口稳定。</summary>
    public static string FormatWeatherBroadcast(string wttrJson, string? preferredPlace = null) =>
        WeatherReport.FormatWeatherBroadcast(wttrJson, preferredPlace);

    public static List<string> BuildWeatherAlerts(
        double tempC, double feelsC, double maxC,
        double precipNowMm, double precipDayMm, int chanceRainPct, string desc) =>
        WeatherReport.BuildWeatherAlerts(tempC, feelsC, maxC, precipNowMm, precipDayMm, chanceRainPct, desc);

    private static string ListDir(string path, int max)
    {
        // 空路径或纯空白 → 桌面，避免模型乱猜盘符
        if (string.IsNullOrWhiteSpace(path))
            path = "桌面";
        path = Expand(path);
        if (!Directory.Exists(path))
            return $"错误：目录不存在 {path}。可试 path=桌面 / Desktop / 文档 / 下载，或先 get_special_folder。";
        max = Math.Clamp(max, 1, 300);
        var sb = new StringBuilder();
        sb.AppendLine($"目录: {path}");
        List<string> dirs;
        List<string> files;
        try
        {
            dirs = Directory.EnumerateDirectories(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
            files = Directory.EnumerateFiles(path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
        }
        catch (Exception ex)
        {
            return $"错误：无法读取目录 {path}（{ex.GetType().Name}: {ex.Message}）。若是权限问题，可换路径或用管理员身份运行。";
        }

        foreach (var d in dirs.Take(max))
            sb.AppendLine($"[目录] {Path.GetFileName(d)}");
        var left = max - Math.Min(dirs.Count, max);
        foreach (var f in files.Take(Math.Max(0, left)))
        {
            var fi = new FileInfo(f);
            sb.AppendLine($"[文件] {fi.Name}  ({fi.Length} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm})");
        }

        sb.AppendLine($"统计: 子目录 {dirs.Count} 条(已列部分), 文件已列 {Math.Min(files.Count, Math.Max(0, left))} 条");
        return sb.ToString().Trim();
    }

    private static string ReadFile(string path, int maxChars)
    {
        path = Expand(path);
        if (!File.Exists(path))
            return $"错误：文件不存在 {path}";
        maxChars = Math.Clamp(maxChars, 500, 100_000);
        var text = File.ReadAllText(path);
        var len = text.Length;
        if (text.Length > maxChars)
            text = text[..maxChars] + $"\n…（已截断，原文 {len} 字）";
        return $"文件: {path}\n大小字符: {len}\n----\n{text}";
    }

    private static string WriteFile(string path, string content, bool createDirs)
    {
        path = Expand(path);
        if (createDirs)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, content ?? string.Empty, Encoding.UTF8);
        return $"已写入: {path}（{Encoding.UTF8.GetByteCount(content ?? string.Empty)} 字节 UTF-8）";
    }

    private static string AppendFile(string path, string content)
    {
        path = Expand(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.AppendAllText(path, content ?? string.Empty, Encoding.UTF8);
        return $"已追加: {path}";
    }

    private static string MovePath(string source, string dest)
    {
        source = Expand(source);
        dest = Expand(dest);
        if (File.Exists(source))
        {
            var ddir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(ddir)) Directory.CreateDirectory(ddir);
            File.Move(source, dest, overwrite: true);
            return $"已移动文件: {source} → {dest}";
        }

        if (Directory.Exists(source))
        {
            Directory.Move(source, dest);
            return $"已移动目录: {source} → {dest}";
        }

        return $"错误：源不存在 {source}";
    }

    private static string CopyPath(string source, string dest)
    {
        source = Expand(source);
        dest = Expand(dest);
        if (File.Exists(source))
        {
            var ddir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(ddir)) Directory.CreateDirectory(ddir);
            File.Copy(source, dest, overwrite: true);
            return $"已复制文件: {source} → {dest}";
        }

        if (Directory.Exists(source))
        {
            CopyDirectory(source, dest);
            return $"已复制目录: {source} → {dest}";
        }

        return $"错误：源不存在 {source}";
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static string DeletePath(string path, bool recursive)
    {
        path = Expand(path);
        // 基础护栏：拒绝明显系统关键路径
        if (IsDangerousPath(path))
            return $"已拒绝：路径过于敏感，请手动处理 → {path}";

        if (File.Exists(path))
        {
            File.Delete(path);
            return $"已删除文件: {path}";
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
            return $"已删除目录: {path} (recursive={recursive})";
        }

        return $"错误：路径不存在 {path}";
    }

    private static string SearchFiles(string root, string pattern, int max)
    {
        root = Expand(root);
        if (!Directory.Exists(root))
            return $"错误：根目录不存在 {root}";
        max = Math.Clamp(max, 1, 200);
        pattern = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern;
        var sb = new StringBuilder();
        sb.AppendLine($"搜索: {root} / {pattern}");
        var count = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                sb.AppendLine(f);
                if (++count >= max) break;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(搜索中断: {ex.Message})");
        }

        sb.AppendLine($"命中: {count}（上限 {max}）");
        return sb.ToString().Trim();
    }

    private static async Task<string> RunCommandAsync(string command, int timeoutSec, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "错误：命令为空";

        // 极高危命令护栏
        var lower = command.ToLowerInvariant();
        string[] blocked =
        [
            "format ", "format\t", "diskpart", "remove-item -recurse c:\\", "rm -rf /",
            "shutdown /s", "shutdown /r", "stop-computer", "restart-computer -force",
        ];
        if (blocked.Any(b => lower.Contains(b, StringComparison.Ordinal)))
            return "已拒绝：命令包含高危系统破坏操作。";

        timeoutSec = Math.Clamp(timeoutSec, 5, 120);
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await proc.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            // 确保异步 stdout/stderr 事件全部排空。
            proc.WaitForExit();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await StopProcessAsync(proc).ConfigureAwait(false);
            return $"错误：命令超时（{timeoutSec}s）已终止。\nSTDOUT:\n{TrimOut(stdout)}\nSTDERR:\n{TrimOut(stderr)}";
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(proc).ConfigureAwait(false);
            throw;
        }

        return $"exit={proc.ExitCode}\nSTDOUT:\n{TrimOut(stdout)}\nSTDERR:\n{TrimOut(stderr)}";
    }

    private static string OpenPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return $"已打开 URL: {path}";
        }

        path = Expand(path);
        if (!File.Exists(path) && !Directory.Exists(path))
            return $"错误：路径不存在 {path}";
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        return $"已打开: {path}";
    }

    private static string GetClipboard()
    {
        string? text = null;
        var thread = new Thread(() =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                    text = System.Windows.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                text = "错误: " + ex.Message;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(3000);
        return string.IsNullOrEmpty(text) ? "(剪贴板为空或不可读)" : text!;
    }

    private static string SetClipboard(string text)
    {
        Exception? err = null;
        var thread = new Thread(() =>
        {
            try { System.Windows.Clipboard.SetText(text ?? string.Empty); }
            catch (Exception ex) { err = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(3000);
        return err is null ? "剪贴板已更新" : "错误: " + err.Message;
    }

    private static string GetSpecialFolder(string name)
    {
        var path = FolderPathResolver.ResolveAlias(name);
        return string.IsNullOrEmpty(path) ? $"错误：未知特殊文件夹 {name}" : path!;
    }

    private static string CreateDirectory(string path)
    {
        path = Expand(path);
        Directory.CreateDirectory(path);
        return $"已创建目录: {path}";
    }

    private static string GetProcessList(string? filter, int max)
    {
        max = Math.Clamp(max, 1, 100);
        filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        var q = Process.GetProcesses().AsEnumerable();
        if (filter is not null)
            q = q.Where(p =>
            {
                try { return p.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            });
        var list = q
            .Select(p =>
            {
                try
                {
                    return new
                    {
                        p.ProcessName,
                        p.Id,
                        Mb = p.WorkingSet64 / 1024.0 / 1024.0,
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(x => x is not null)
            .OrderByDescending(x => x!.Mb)
            .Take(max)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("进程名\tPID\t内存MB");
        foreach (var p in list)
            sb.AppendLine($"{p!.ProcessName}\t{p.Id}\t{p.Mb:F1}");
        return sb.ToString().Trim();
    }

    // ---------- helpers ----------

    private static JsonObject Tool(string name, string description, JsonObject parameters, string[]? required = null)
    {
        if (parameters["type"] is null)
        {
            // Props() 已写入 type=object；空对象补齐
            if (!parameters.ContainsKey("type"))
            {
                parameters["type"] = "object";
                if (!parameters.ContainsKey("properties"))
                    parameters["properties"] = new JsonObject();
            }
        }

        if (required is { Length: > 0 })
        {
            var arr = new JsonArray();
            foreach (var r in required) arr.Add(r);
            parameters["required"] = arr;
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = parameters,
            },
        };
    }

    private static JsonObject Props(params (string Name, string Type, string Desc)[] items)
    {
        var props = new JsonObject();
        foreach (var (name, type, desc) in items)
        {
            props[name] = new JsonObject
            {
                ["type"] = type,
                ["description"] = desc,
            };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
        };
    }

    private static string Str(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var n) || n is null)
            return string.Empty;
        return n.GetValue<string>() ?? n.ToJsonString().Trim('"');
    }

    private static int Int(JsonObject args, string key, int fallback)
    {
        if (!args.TryGetPropertyValue(key, out var n) || n is null)
            return fallback;
        try
        {
            return n.GetValue<int>();
        }
        catch
        {
            return int.TryParse(n.ToString(), out var v) ? v : fallback;
        }
    }

    private static bool Bool(JsonObject args, string key, bool fallback)
    {
        if (!args.TryGetPropertyValue(key, out var n) || n is null)
            return fallback;
        try
        {
            return n.GetValue<bool>();
        }
        catch
        {
            return bool.TryParse(n.ToString(), out var v) ? v : fallback;
        }
    }

    private static string G(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.String => p.GetString() ?? "",
                JsonValueKind.Number => p.ToString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => p.ToString(),
            }
            : "";

    private static string Expand(string path) => FolderPathResolver.Expand(path);

    private static string GetDownloadsPath() => FolderPathResolver.GetDownloadsPath();

    private static bool IsDangerousPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.SystemDirectory,
                Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\",
            };
            // 禁止删除盘符根与 Windows 目录
            if (Regex.IsMatch(full, @"^[A-Za-z]:\\?$"))
                return true;
            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                var rr = Path.GetFullPath(r).TrimEnd('\\', '/');
                if (full.Equals(rr, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (full.StartsWith(rr + "\\", StringComparison.OrdinalIgnoreCase)
                    && (rr.EndsWith("Windows", StringComparison.OrdinalIgnoreCase)
                        || rr.EndsWith("System32", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
        }
        catch
        {
            return true;
        }

        return false;
    }

    private static async Task StopProcessAsync(Process proc)
    {
        try
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 权限不足时 Kill 可能失败，后续等待仍必须有上限。
        }

        using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await proc.WaitForExitAsync(cleanupCts.Token).ConfigureAwait(false); }
        catch { /* 清理超时不再阻塞聊天取消 */ }
    }

    private static string TrimOut(StringBuilder sb)
    {
        var s = sb.ToString();
        if (s.Length > 12000)
            return s[..12000] + "\n…(输出过长已截断)";
        return s.Length == 0 ? "(空)" : s;
    }
}
