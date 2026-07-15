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
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion-Agent/1.2");
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
            "根据公网 IP 与本机网络信息推断当前位置（城市/地区/运营商/IP）。用户问「我在哪」「定位」时必须调用。",
            new JsonObject()),
        Tool("get_weather",
            "查询实时天气。可指定 city；不指定则先按 IP 定位再查天气。用户问天气时必须调用。",
            Props(("city", "string", "城市名，如 北京、上海、深圳；可空则自动定位"))),
        Tool("list_dir",
            "列出目录内容（文件与子文件夹）。",
            Props(
                ("path", "string", "目录绝对路径，如 C:\\Users\\Name\\Documents"),
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
        Tool("notify_user",
            "向对话返回一条给用户看的中间状态说明（不会弹系统通知）。用于说明正在执行的步骤。",
            Props(("message", "string", "说明文字")),
            required: ["message"]),
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
                "notify_user" => "OK: " + Str(args, "message"),
                _ => $"错误：未知工具 {name}",
            };
        }
        catch (Exception ex)
        {
            return $"错误：{ex.GetType().Name}: {ex.Message}";
        }
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
        sb.AppendLine("【公网 IP 定位】");
        // 多个免费源，提高成功率
        var sources = new[]
        {
            "http://ip-api.com/json/?lang=zh-CN&fields=status,message,country,regionName,city,district,zip,lat,lon,isp,org,as,query,timezone",
            "https://ipapi.co/json/",
        };

        foreach (var url in sources)
        {
            try
            {
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (url.Contains("ip-api", StringComparison.Ordinal))
                {
                    if (root.TryGetProperty("status", out var st) && st.GetString() == "fail")
                        continue;
                    sb.AppendLine($"IP: {G(root, "query")}");
                    sb.AppendLine($"国家: {G(root, "country")}");
                    sb.AppendLine($"省/州: {G(root, "regionName")}");
                    sb.AppendLine($"城市: {G(root, "city")}");
                    sb.AppendLine($"区: {G(root, "district")}");
                    sb.AppendLine($"经纬度: {G(root, "lat")}, {G(root, "lon")}");
                    sb.AppendLine($"ISP: {G(root, "isp")}");
                    sb.AppendLine($"组织: {G(root, "org")}");
                    sb.AppendLine($"时区: {G(root, "timezone")}");
                    sb.AppendLine("说明: 基于公网 IP 的近似位置，精度到城市级；公司出口 IP 可能显示机房/总部城市。");
                    return sb.ToString().Trim();
                }

                // ipapi.co
                sb.AppendLine($"IP: {G(root, "ip")}");
                sb.AppendLine($"国家: {G(root, "country_name")}");
                sb.AppendLine($"省/州: {G(root, "region")}");
                sb.AppendLine($"城市: {G(root, "city")}");
                sb.AppendLine($"经纬度: {G(root, "latitude")}, {G(root, "longitude")}");
                sb.AppendLine($"ISP/Org: {G(root, "org")}");
                sb.AppendLine($"时区: {G(root, "timezone")}");
                sb.AppendLine("说明: 基于公网 IP 的近似位置。");
                return sb.ToString().Trim();
            }
            catch
            {
                // try next
            }
        }

        sb.AppendLine("公网定位暂时失败（网络或接口限制）。仍可根据本机网卡与公司网络环境推断。");
        return sb.ToString().Trim();
    }

    private static async Task<string> GetWeatherAsync(string? city, CancellationToken ct)
    {
        city = string.IsNullOrWhiteSpace(city) ? null : city.Trim();
        string? lat = null, lon = null, place = city;

        if (city is null)
        {
            try
            {
                var locJson = await Http.GetStringAsync(
                    "http://ip-api.com/json/?lang=zh-CN&fields=status,city,lat,lon,regionName,country,district,query", ct)
                    .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(locJson);
                var root = doc.RootElement;
                if (G(root, "status") != "fail")
                {
                    place = string.Join(" ", new[]
                    {
                        G(root, "country"), G(root, "regionName"), G(root, "city"), G(root, "district"),
                    }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    lat = G(root, "lat");
                    lon = G(root, "lon");
                    city = G(root, "city");
                }
            }
            catch
            {
                // fall through
            }
        }

        // wttr.in 免费天气，支持中文 + 逐时降水
        try
        {
            var q = !string.IsNullOrWhiteSpace(lat) && !string.IsNullOrWhiteSpace(lon)
                ? $"{lat},{lon}"
                : (city ?? "Beijing");
            var url = $"https://wttr.in/{Uri.EscapeDataString(q)}?format=j1&lang=zh";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return WeatherReport.FormatWeatherBroadcast(body, place);
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
        path = Expand(path);
        if (!Directory.Exists(path))
            return $"错误：目录不存在 {path}";
        max = Math.Clamp(max, 1, 300);
        var sb = new StringBuilder();
        sb.AppendLine($"目录: {path}");
        var dirs = Directory.EnumerateDirectories(path).OrderBy(x => x).Take(max).ToList();
        var files = Directory.EnumerateFiles(path).OrderBy(x => x).Take(max).ToList();
        foreach (var d in dirs.Take(max))
            sb.AppendLine($"[目录] {Path.GetFileName(d)}");
        var left = max - Math.Min(dirs.Count, max);
        foreach (var f in files.Take(Math.Max(0, left)))
        {
            var fi = new FileInfo(f);
            sb.AppendLine($"[文件] {fi.Name}  ({fi.Length} bytes, {fi.LastWriteTime:yyyy-MM-dd HH:mm})");
        }

        sb.AppendLine($"统计: {dirs.Count}+ 子目录(列出部分), 文件已列 {Math.Min(files.Count, Math.Max(0, left))} 条");
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
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + QuotePs(command),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        var finished = await Task.Run(() => proc.WaitForExit(timeoutSec * 1000), ct).ConfigureAwait(false);
        if (!finished)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return $"错误：命令超时（{timeoutSec}s）已终止。\nSTDOUT:\n{TrimOut(stdout)}\nSTDERR:\n{TrimOut(stderr)}";
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
        thread.Start();
        thread.Join(3000);
        return err is null ? "剪贴板已更新" : "错误: " + err.Message;
    }

    private static string GetSpecialFolder(string name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        var path = n switch
        {
            "desktop" or "桌面" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            "documents" or "docs" or "文档" or "我的文档" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "downloads" or "download" or "下载" => GetDownloadsPath(),
            "pictures" or "图片" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "music" or "音乐" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "videos" or "视频" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "userprofile" or "home" or "用户" or "用户目录" => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "appdata" or "roaming" => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "localappdata" or "local" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "temp" or "临时" => Path.GetTempPath(),
            _ => "",
        };
        return string.IsNullOrEmpty(path) ? $"错误：未知特殊文件夹 {name}" : path;
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

    private static string Expand(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (path.StartsWith("~/", StringComparison.Ordinal) || path == "~")
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/', '\\'));
        path = Environment.ExpandEnvironmentVariables(path);
        // 中文别名快捷
        path = path.Replace("/桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop), StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(path);
    }

    private static string GetDownloadsPath()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var dl = Path.Combine(home, "Downloads");
            return Directory.Exists(dl) ? dl : home;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
    }

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

    private static string QuotePs(string command) =>
        "'" + command.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string TrimOut(StringBuilder sb)
    {
        var s = sb.ToString();
        if (s.Length > 12000)
            return s[..12000] + "\n…(输出过长已截断)";
        return s.Length == 0 ? "(空)" : s;
    }
}
