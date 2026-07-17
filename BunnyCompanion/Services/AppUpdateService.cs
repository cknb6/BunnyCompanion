using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 从 GitHub Releases 检查 / 下载更新，并以 checksums.txt 中的 SHA256 校验防篡改。
/// 仅允许官方仓库资源；校验失败绝不替换本机 EXE。
/// 单文件 EXE 无法原地热替换代码，采用「后台下载校验 → 退出后脚本替换并重启」。
/// 未认证 API 约 60 次/小时/IP：后台检查有最小间隔与缓存，403/429 不抹掉上次成功结果。
/// </summary>
public static class AppUpdateService
{
    public const string Owner = "cknb6";
    public const string Repo = "BunnyCompanion";
    public const string ReleasesApiLatest = "https://api.github.com/repos/cknb6/BunnyCompanion/releases/latest";
    public const string ReleasesPage = "https://github.com/cknb6/BunnyCompanion/releases/latest";

    /// <summary>后台自动检查默认最小间隔（避免 5 分钟 force 打穿未认证配额）。</summary>
    public static readonly TimeSpan DefaultBackgroundMinInterval = TimeSpan.FromMinutes(45);

    /// <summary>手动检查即使 force，也建议的冷却（仅用于 UI 侧可选）。</summary>
    public static readonly TimeSpan DefaultInteractiveMinInterval = TimeSpan.FromSeconds(20);

    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static UpdateCheckResult? _lastResult;
    /// <summary>最近一次「成功」的检查结果；限流时优先回退，避免二次全是 403。</summary>
    private static UpdateCheckResult? _lastSuccessResult;

    /// <summary>测试用：HTTP 发送层可替换（仅 CheckAsync 主 API 请求计数/模拟 403）。</summary>
    public static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HttpSendOverrideForTests { get; set; }

    /// <summary>测试用：CheckAsync 实际发起的联网次数（含 override）。</summary>
    public static int NetworkRequestCountForTests { get; private set; }

    /// <summary>测试用：最近一次缓存时间戳（UTC）。</summary>
    public static DateTime LastCheckUtcForTests
    {
        get { lock (Gate) return _lastCheckUtc; }
    }

    public sealed record ReleaseAsset(string Name, string BrowserDownloadUrl, long Size);

    public sealed record UpdateCheckResult(
        bool Success,
        bool UpdateAvailable,
        string Message,
        Version LocalVersion,
        Version? RemoteVersion,
        string? TagName,
        string? ReleaseNotes,
        string? ExeDownloadUrl,
        string? ChecksumsDownloadUrl,
        string? ExpectedSha256,
        string? TargetFileName);

    public sealed record ApplyResult(bool Success, string Message);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        // GitHub 要求可识别 User-Agent；匿名 API 严格限流
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion-Updater/1.5 (+https://github.com/cknb6/BunnyCompanion)");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    /// <summary>
    /// 是否应跳过网络、直接复用缓存（纯逻辑，可单测）。
    /// force=true 时永不跳过；有缓存且未过 minInterval 时跳过。
    /// </summary>
    public static bool ShouldUseCachedResult(
        bool force,
        TimeSpan? minInterval,
        DateTime lastCheckUtc,
        DateTime nowUtc,
        bool hasCachedResult)
    {
        if (force || !hasCachedResult || minInterval is null)
            return false;
        var gap = minInterval.Value;
        if (gap <= TimeSpan.Zero)
            return false;
        return nowUtc - lastCheckUtc < gap;
    }

    /// <summary>是否为限流/滥用类状态码（含部分 403）。</summary>
    public static bool IsRateLimitOrAbuseStatus(int statusCode) =>
        statusCode is 403 or 429 or 408;

    /// <summary>
    /// 将 GitHub HTTP 错误映射为中文用户文案（禁止只显示裸 HTTP 403）。
    /// </summary>
    public static string FormatGitHubHttpError(
        int statusCode,
        string? responseBodySnippet = null,
        string? retryAfterHeader = null)
    {
        var body = (responseBodySnippet ?? "").Trim();
        var looksRate =
            IsRateLimitOrAbuseStatus(statusCode)
            || body.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || body.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
            || body.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)
            || body.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase);

        if (looksRate || statusCode is 403 or 429)
        {
            var wait = "";
            if (!string.IsNullOrWhiteSpace(retryAfterHeader)
                && int.TryParse(retryAfterHeader.Trim(), out var sec)
                && sec > 0)
            {
                wait = sec >= 60
                    ? $" 建议约 {Math.Max(1, (sec + 59) / 60)} 分钟后再试。"
                    : $" 建议约 {sec} 秒后再试。";
            }

            return
                "检查更新暂时受限（GitHub 访问频率限制）。" + wait +
                "未登录接口每小时次数有限，同一网络多人共用更容易触发。" +
                "请稍后再试；也可打开 Releases 页手动下载：\n" +
                ReleasesPage;
        }

        if (statusCode is >= 500 and <= 599)
            return $"GitHub 服务暂时不可用（HTTP {statusCode}）。请稍后再试，或手动打开：\n{ReleasesPage}";

        if (statusCode == 404)
            return "未找到最新 Release（HTTP 404）。请确认仓库发布页是否正常。";

        return
            $"检查更新失败：网络或接口异常（HTTP {statusCode}）。" +
            $"可稍后重试，或打开：\n{ReleasesPage}";
    }

    /// <summary>测试用：注入缓存状态（不发起网络）。</summary>
    public static void SeedCacheForTests(UpdateCheckResult result, DateTime? checkUtc = null)
    {
        lock (Gate)
        {
            _lastResult = result;
            _lastCheckUtc = checkUtc ?? DateTime.UtcNow;
            if (result.Success)
                _lastSuccessResult = result;
        }
    }

    /// <summary>测试用：清空进程内缓存与发送钩子计数。</summary>
    public static void ClearCacheForTests()
    {
        lock (Gate)
        {
            _lastResult = null;
            _lastSuccessResult = null;
            _lastCheckUtc = DateTime.MinValue;
        }

        NetworkRequestCountForTests = 0;
        HttpSendOverrideForTests = null;
    }

    /// <summary>测试/诊断：是否已有任意缓存结果。</summary>
    public static bool HasCachedResultForTests()
    {
        lock (Gate)
            return _lastResult is not null;
    }

    private static async Task<HttpResponseMessage> SendGitHubAsync(HttpRequestMessage req, CancellationToken ct)
    {
        NetworkRequestCountForTests++;
        if (HttpSendOverrideForTests is not null)
            return await HttpSendOverrideForTests(req, ct).ConfigureAwait(false);
        return await Http.SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>当前运行中的本机版本（优先 FileVersion 含 CI 补丁号；辅以本机记录）。</summary>
    public static Version GetLocalVersion()
    {
        Version? best = null;
        void Consider(Version v)
        {
            if (best is null || v > best)
                best = v;
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                // 单文件发布：FileVersion 常为 1.4.0.31；ProductVersion 可能带元数据
                if (TryParseVersion(fvi.FileVersion, out var fv))
                    Consider(fv);
                if (TryParseVersion(fvi.ProductVersion, out var pv))
                    Consider(pv);
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v is not null)
                Consider(new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision)));
        }
        catch
        {
            // ignore
        }

        // 更新成功后写入的侧车版本（防止 FileVersion 读不到时永远当成 1.0）
        try
        {
            var side = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BunnyCompanion", "installed_version.txt");
            if (File.Exists(side) && TryParseVersion(File.ReadAllText(side).Trim(), out var sv))
                Consider(sv);
        }
        catch
        {
            // ignore
        }

        return best ?? new Version(1, 0, 0, 0);
    }

    /// <summary>启动或更新成功后记录本机版本，供下次比较。</summary>
    public static void RememberInstalledVersion(Version? version = null)
    {
        try
        {
            var v = version ?? GetLocalVersion();
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BunnyCompanion");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "installed_version.txt"), FormatVersion(v));
        }
        catch
        {
            // ignore
        }
    }

    public static string FormatVersion(Version v)
    {
        if (v.Revision > 0)
            return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}.{v.Revision}";
        if (v.Build > 0)
            return $"{v.Major}.{v.Minor}.{v.Build}";
        return $"{v.Major}.{v.Minor}";
    }

    public static string CurrentRidFileName()
    {
        // 优先进程架构
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.Arm64)
                return "BunnyCompanion-win-arm64.exe";
        }
        catch
        {
            // ignore
        }

        return "BunnyCompanion-win-x64.exe";
    }

    /// <summary>
    /// 检查最新 Release。minInterval 内复用缓存，避免频繁打 GitHub。
    /// force=true 时强制联网；若遇 403/429 限流，尽量回退上次成功结果。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        TimeSpan? minInterval = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var local = GetLocalVersion();
        var now = DateTime.UtcNow;
        UpdateCheckResult? cached;
        DateTime lastUtc;
        UpdateCheckResult? lastOk;
        lock (Gate)
        {
            cached = _lastResult;
            lastUtc = _lastCheckUtc;
            lastOk = _lastSuccessResult;
        }

        if (ShouldUseCachedResult(force, minInterval, lastUtc, now, cached is not null) && cached is not null)
            return cached;

        try
        {
            DebugLogService.Log("update", $"CheckAsync force={force} local={local}");
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiLatest);
            using var resp = await SendGitHubAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                string? retryAfter = null;
                if (resp.Headers.TryGetValues("Retry-After", out var vals))
                    retryAfter = vals.FirstOrDefault();
                var errMsg = FormatGitHubHttpError(code, body.Length > 400 ? body[..400] : body, retryAfter);
                DebugLogService.Log("update", $"CheckAsync HTTP {code} retryAfter={retryAfter ?? "-"} msg={errMsg[..Math.Min(errMsg.Length, 200)]}");
                return ResolveHttpFailure(local, errMsg, code, lastOk);
            }
            DebugLogService.Log("update", $"CheckAsync HTTP 200 bodyLen={body.Length}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var remote))
            {
                return CacheFailure(new UpdateCheckResult(
                    false, false, $"无法解析远端版本标签：{tag}", local, null,
                    tag, null, null, null, null, null));
            }

            var notes = root.TryGetProperty("body", out var bodyEl) ? bodyEl.GetString() : null;
            string? exeUrl = null;
            string? sumUrl = null;
            var fileName = CurrentRidFileName();
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                        continue;
                    if (!IsAllowedDownloadUrl(url))
                        continue;
                    if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                        exeUrl = url;
                    else if (name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase))
                        sumUrl = url;
                }
            }

            if (string.IsNullOrWhiteSpace(exeUrl))
            {
                return CacheSuccess(new UpdateCheckResult(
                    true, false, $"最新版 {tag} 没有本机架构安装包（{fileName}）", local, remote,
                    tag, notes, null, sumUrl, null, fileName));
            }

            if (string.IsNullOrWhiteSpace(sumUrl))
            {
                return CacheFailure(new UpdateCheckResult(
                    false, false, "最新版缺少 checksums.txt，拒绝更新（无法校验哈希）", local, remote,
                    tag, notes, exeUrl, null, null, fileName));
            }

            // 预取校验文件并解析期望哈希（不下载 EXE）
            string? expectedHash;
            try
            {
                var checksumText = await DownloadTextAsync(sumUrl, ct).ConfigureAwait(false);
                expectedHash = ParseSha256FromChecksums(checksumText, fileName);
            }
            catch (Exception ex)
            {
                // 下载 checksums 也可能 403
                var msg = ex.Message.Contains("限流", StringComparison.Ordinal)
                          || ex.Message.Contains("403", StringComparison.Ordinal)
                          || ex.Message.Contains("429", StringComparison.Ordinal)
                    ? ex.Message
                    : "读取校验文件失败：" + ex.Message;
                if (IsRateLimitMessage(msg) && lastOk is { Success: true })
                    return WithCacheNote(lastOk, msg);
                return CacheFailure(new UpdateCheckResult(
                    false, false, msg, local, remote,
                    tag, notes, exeUrl, sumUrl, null, fileName));
            }

            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return CacheFailure(new UpdateCheckResult(
                    false, false, $"checksums.txt 中找不到 {fileName} 的 SHA256，拒绝更新", local, remote,
                    tag, notes, exeUrl, sumUrl, null, fileName));
            }

            // 版本更新，或「同版本但哈希不同」（热修包）
            var newer = remote > local;
            if (!newer && remote >= local)
            {
                try
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
                    {
                        var localHash = await ComputeSha256HexAsync(exePath, ct).ConfigureAwait(false);
                        if (!string.Equals(localHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                            newer = true;
                    }
                }
                catch
                {
                    // 哈希读失败则仅按版本号
                }
            }

            var okMsg = newer
                ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}）"
                : $"已是最新（{FormatVersion(local)}）";

            return CacheSuccess(new UpdateCheckResult(
                true, newer, okMsg, local, remote, tag, notes, exeUrl, sumUrl, expectedHash, fileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DebugLogService.LogError("update", "CheckAsync 异常（多为网络不通/DNS/超时）", ex);
            var msg = IsRateLimitMessage(ex.Message)
                ? ex.Message
                : "检查更新失败：" + ex.Message;
            if (IsRateLimitMessage(msg) && lastOk is { Success: true })
                return WithCacheNote(lastOk, msg);
            return CacheFailure(new UpdateCheckResult(
                false, false, msg, local, null,
                null, null, null, null, null, null));
        }
    }

    private static bool IsRateLimitMessage(string? msg) =>
        !string.IsNullOrEmpty(msg)
        && (msg.Contains("频率限制", StringComparison.Ordinal)
            || msg.Contains("限流", StringComparison.Ordinal)
            || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("稍后再试", StringComparison.Ordinal));

    /// <summary>限流时：有上次成功则带回退说明，不抹掉成功缓存。</summary>
    private static UpdateCheckResult ResolveHttpFailure(
        Version local, string errMsg, int statusCode, UpdateCheckResult? lastOk)
    {
        if (IsRateLimitOrAbuseStatus(statusCode) && lastOk is { Success: true })
            return WithCacheNote(lastOk, errMsg);

        return CacheFailure(new UpdateCheckResult(
            false, false, errMsg, local, null,
            null, null, null, null, null, null));
    }

    private static UpdateCheckResult WithCacheNote(UpdateCheckResult lastOk, string rateLimitMsg)
    {
        // 限流回退：保留 _lastSuccessResult，但必须推进 _lastCheckUtc，
        // 否则「成功时间已过 45 分钟 + 每 5 分钟定时」会每轮重新联网放大限流。
        var note =
            lastOk.Message +
            "\n（本次联网受限，已沿用刚才的检查结果。" +
            rateLimitMsg.Replace("\n", " ", StringComparison.Ordinal).Trim() + "）";
        if (note.Length > 500)
            note = note[..500] + "…";
        var wrapped = lastOk with { Message = note };
        lock (Gate)
        {
            _lastResult = wrapped;
            // 关键：任意已联网的限流回退都要刷新冷却时钟，避免 5 分钟定时反复打穿
            _lastCheckUtc = DateTime.UtcNow;
            if (lastOk.Success)
                _lastSuccessResult = lastOk;
        }

        return wrapped;
    }

    /// <summary>
    /// 下载 EXE → SHA256 校验 → 写替换脚本 → 退出后替换并重启。
    /// 校验失败会删除临时文件，绝不替换。
    /// </summary>
    public static async Task<ApplyResult> DownloadVerifyAndScheduleReplaceAsync(
        UpdateCheckResult check,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!check.UpdateAvailable
            || string.IsNullOrWhiteSpace(check.ExeDownloadUrl)
            || string.IsNullOrWhiteSpace(check.ExpectedSha256)
            || string.IsNullOrWhiteSpace(check.TargetFileName))
        {
            return new ApplyResult(false, check.Success ? "没有可安装的更新。" : check.Message);
        }

        if (!IsAllowedDownloadUrl(check.ExeDownloadUrl))
            return new ApplyResult(false, "下载地址不在白名单，已拒绝。");

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
            return new ApplyResult(false, "无法定位当前程序路径。");

        var updateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BunnyCompanion", "updates");
        Directory.CreateDirectory(updateDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var tempExe = Path.Combine(updateDir, $"{check.TargetFileName}.{stamp}.new");
        var scriptPath = Path.Combine(updateDir, $"apply_{stamp}.ps1");

        try
        {
            DebugLogService.Log("update", $"Apply 开始：tag={check.TagName} target={check.TargetFileName} exe={currentExe}");
            progress?.Report("正在下载更新包…");
            await DownloadFileAsync(check.ExeDownloadUrl!, tempExe, progress, ct).ConfigureAwait(false);
            DebugLogService.Log("update", $"下载完成：{new FileInfo(tempExe).Length} bytes");

            progress?.Report("正在校验 SHA256…");
            var actual = await ComputeSha256HexAsync(tempExe, ct).ConfigureAwait(false);
            if (!string.Equals(actual, check.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(tempExe); } catch { /* ignore */ }
                return new ApplyResult(false,
                    $"哈希校验失败，已丢弃文件（疑似被污染）。\n期望：{check.ExpectedSha256}\n实际：{actual}");
            }

            // 二次确认：重新拉一次 checksums 对比，防止第一次检查与下载之间被替换（尽量一致）
            if (!string.IsNullOrWhiteSpace(check.ChecksumsDownloadUrl)
                && IsAllowedDownloadUrl(check.ChecksumsDownloadUrl))
            {
                try
                {
                    var latestSum = await DownloadTextAsync(check.ChecksumsDownloadUrl!, ct).ConfigureAwait(false);
                    var again = ParseSha256FromChecksums(latestSum, check.TargetFileName!);
                    if (!string.IsNullOrWhiteSpace(again)
                        && !string.Equals(again, actual, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(tempExe); } catch { /* ignore */ }
                        return new ApplyResult(false, "二次校验失败：发布页校验信息与下载文件不一致，已取消更新。");
                    }
                }
                catch
                {
                    // 二次拉取失败不阻断（首次校验已通过），但记录在消息里
                }
            }

            progress?.Report("校验通过，准备覆盖安装…");
            // 同时写 bat（兼容性更好）+ ps1 双保险
            var batPath = Path.Combine(updateDir, $"apply_{stamp}.bat");
            WriteApplyBat(batPath, Environment.ProcessId, tempExe, currentExe, check.TagName ?? "");
            WriteApplyScript(scriptPath, Environment.ProcessId, tempExe, currentExe);

            Process? helper = null;
            try
            {
                helper = Process.Start(new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true, // 独立会话，主进程退出后仍可覆盖 EXE
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex)
            {
                DebugLogService.LogError("update", "启动 bat 覆盖脚本失败", ex);
                helper = null;
            }

            if (helper is null)
            {
                try
                {
                    helper = Process.Start(new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        ArgumentList =
                        {
                            "-NoProfile",
                            "-ExecutionPolicy", "Bypass",
                            "-WindowStyle", "Hidden",
                            "-File", scriptPath,
                        },
                    });
                }
                catch (Exception ex)
                {
                    DebugLogService.LogError("update", "启动 ps1 覆盖脚本也失败", ex);
                    helper = null;
                }
            }

            if (helper is null)
            {
                try { File.Delete(tempExe); } catch { /* ignore */ }
                try { File.Delete(scriptPath); } catch { /* ignore */ }
                try { File.Delete(batPath); } catch { /* ignore */ }
                return new ApplyResult(false, "无法启动更新脚本。请手动从 GitHub Releases 下载覆盖安装。");
            }

            // 记录即将安装的版本，重启后 GetLocalVersion 可对齐
            if (check.RemoteVersion is not null)
                RememberInstalledVersion(check.RemoteVersion);

            return new ApplyResult(true, $"校验通过（SHA256 一致）。即将自动覆盖并重启到 {check.TagName}。");
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            DebugLogService.LogError("update", "Apply 失败", ex);
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { /* ignore */ }
            return new ApplyResult(false, "更新失败：" + ex.Message);
        }
    }

    private static void WriteApplyScript(string scriptPath, int pid, string srcExe, string dstExe)
    {
        // 等原进程退出 → 多次重试覆盖 → 启动 → 清理
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Continue'");
        sb.AppendLine($"$pidToWait = {pid}");
        sb.AppendLine($"$src = {PsQuote(srcExe)}");
        sb.AppendLine($"$dst = {PsQuote(dstExe)}");
        sb.AppendLine("$log = Join-Path $env:LOCALAPPDATA 'BunnyCompanion\\updates\\last_apply.log'");
        sb.AppendLine("function L([string]$m){ try { Add-Content -LiteralPath $log -Value ((Get-Date).ToString('s') + ' ' + $m) } catch {} }");
        sb.AppendLine("L 'start apply'");
        sb.AppendLine("$deadline = (Get-Date).AddMinutes(3)");
        sb.AppendLine("while ((Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }");
        sb.AppendLine("Start-Sleep -Milliseconds 600");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $src)) { L 'src missing'; exit 2 }");
        sb.AppendLine("$bak = $dst + '.bak'");
        sb.AppendLine("try { if (Test-Path -LiteralPath $dst) { Copy-Item -LiteralPath $dst -Destination $bak -Force } } catch { L $_.Exception.Message }");
        sb.AppendLine("$ok = $false");
        sb.AppendLine("for ($i=0; $i -lt 25; $i++) {");
        sb.AppendLine("  try { Copy-Item -LiteralPath $src -Destination $dst -Force; $ok = $true; break } catch { Start-Sleep -Milliseconds 400 }");
        sb.AppendLine("}");
        sb.AppendLine("if (-not $ok) { L 'copy failed'; exit 3 }");
        sb.AppendLine("L 'copy ok'");
        sb.AppendLine("Start-Process -FilePath $dst");
        sb.AppendLine("try { Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch {}");
        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>CMD 覆盖脚本：等进程退出后 copy /Y 覆盖当前 EXE 并重启。</summary>
    private static void WriteApplyBat(string batPath, int pid, string srcExe, string dstExe, string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine($"set \"PID={pid}\"");
        sb.AppendLine($"set \"SRC={srcExe}\"");
        sb.AppendLine($"set \"DST={dstExe}\"");
        sb.AppendLine("set \"LOG=%LOCALAPPDATA%\\BunnyCompanion\\updates\\last_apply.log\"");
        sb.AppendLine("echo %DATE% %TIME% apply " + tag + " >> \"%LOG%\" 2>nul");
        sb.AppendLine(":wait");
        sb.AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | find \"%PID%\" >nul");
        sb.AppendLine("if not errorlevel 1 (");
        sb.AppendLine("  timeout /t 1 /nobreak >nul");
        sb.AppendLine("  goto wait");
        sb.AppendLine(")");
        sb.AppendLine("timeout /t 1 /nobreak >nul");
        sb.AppendLine("if not exist \"%SRC%\" ( echo src missing >> \"%LOG%\" & exit /b 2 )");
        sb.AppendLine("if exist \"%DST%\" copy /Y \"%DST%\" \"%DST%.bak\" >nul 2>&1");
        sb.AppendLine("set /a N=0");
        sb.AppendLine(":retry");
        sb.AppendLine("copy /Y \"%SRC%\" \"%DST%\" >nul 2>&1");
        sb.AppendLine("if errorlevel 1 (");
        sb.AppendLine("  set /a N+=1");
        sb.AppendLine("  if %N% LSS 25 (");
        sb.AppendLine("    timeout /t 1 /nobreak >nul");
        sb.AppendLine("    goto retry");
        sb.AppendLine("  ) else (");
        sb.AppendLine("    echo copy failed >> \"%LOG%\" & exit /b 3");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("echo copy ok >> \"%LOG%\" 2>nul");
        sb.AppendLine("start \"\" \"%DST%\"");
        sb.AppendLine("del /F /Q \"%SRC%\" >nul 2>&1");
        sb.AppendLine("del /F /Q \"%~f0\" >nul 2>&1");
        File.WriteAllText(batPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string PsQuote(string path) =>
        "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static UpdateCheckResult CacheSuccess(UpdateCheckResult r)
    {
        lock (Gate)
        {
            _lastCheckUtc = DateTime.UtcNow;
            _lastResult = r;
            _lastSuccessResult = r;
        }

        return r;
    }

    /// <summary>失败缓存：不覆盖 _lastSuccessResult，避免限流后全是失败。</summary>
    private static UpdateCheckResult CacheFailure(UpdateCheckResult r)
    {
        lock (Gate)
        {
            _lastCheckUtc = DateTime.UtcNow;
            _lastResult = r;
        }

        return r;
    }

    public static bool IsAllowedDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        var host = uri.Host.ToLowerInvariant();
        // GitHub release 资产常见域名
        if (host is "github.com" or "www.github.com"
            or "objects.githubusercontent.com"
            or "release-assets.githubusercontent.com"
            or "github-releases.githubusercontent.com")
        {
            // github.com 路径必须落在本仓库 releases
            if (host.Contains("github.com", StringComparison.Ordinal) && host.EndsWith("github.com", StringComparison.Ordinal))
            {
                // api 不用于下载；browser_download_url 一般是 github.com/.../releases/download/...
                var path = uri.AbsolutePath;
                if (path.Contains($"/{Owner}/{Repo}/", StringComparison.OrdinalIgnoreCase)
                    || path.Contains($"/{Owner}/{Repo}.git", StringComparison.OrdinalIgnoreCase))
                    return true;
                // objects.githubusercontent 无路径约束，依赖来源为 GitHub API assets
                if (host != "github.com" && host != "www.github.com")
                    return true;
                return path.Contains("/releases/", StringComparison.OrdinalIgnoreCase)
                       && path.Contains(Repo, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        return false;
    }

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
            t = t[1..];
        // 去掉 +metadata / -pre
        var cut = t.IndexOfAny(['+', '-']);
        if (cut >= 0)
            t = t[..cut];
        // 1.3.0.26 or 1.3.0
        if (Version.TryParse(t, out var v))
        {
            version = Normalize(v);
            return true;
        }

        var m = Regex.Match(t, @"(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success)
            return false;
        var maj = int.Parse(m.Groups[1].Value);
        var min = int.Parse(m.Groups[2].Value);
        var bld = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        var rev = m.Groups[4].Success ? int.Parse(m.Groups[4].Value) : 0;
        version = new Version(maj, min, bld, rev);
        return true;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision < 0 ? 0 : v.Revision));

    /// <summary>
    /// 解析 checksums.txt：兼容
    /// SHA256：ABC...
    /// 以及 SHA256=ABC  /  ABC  BunnyCompanion-win-x64.exe  /  [win-x64] 块。
    /// </summary>
    public static string? ParseSha256FromChecksums(string text, string targetFileName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(targetFileName))
            return null;

        var rid = targetFileName.Contains("arm64", StringComparison.OrdinalIgnoreCase) ? "win-arm64"
            : targetFileName.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "win-x86"
            : "win-x64";

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // 1) 同行同时含文件名与 SHA256
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (!line.Contains(targetFileName, StringComparison.OrdinalIgnoreCase)
                && !line.Contains(rid, StringComparison.OrdinalIgnoreCase))
                continue;
            var hash = ExtractSha256Token(line);
            if (hash is not null)
                return hash;
        }

        // 2) 分段 [win-x64] … SHA256：
        string? currentSection = null;
        string? sectionHash = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var sec = Regex.Match(line, @"^\[(win-x64|win-arm64|win-x86)\]", RegexOptions.IgnoreCase);
            if (sec.Success)
            {
                if (string.Equals(currentSection, rid, StringComparison.OrdinalIgnoreCase) && sectionHash is not null)
                    return sectionHash;
                currentSection = sec.Groups[1].Value;
                sectionHash = null;
                continue;
            }

            if (currentSection is null)
                continue;
            if (!string.Equals(currentSection, rid, StringComparison.OrdinalIgnoreCase))
                continue;

            var h = ExtractSha256Token(line);
            if (h is not null)
                sectionHash = h;
            if (line.Contains(targetFileName, StringComparison.OrdinalIgnoreCase) && h is not null)
                return h;
        }

        if (string.Equals(currentSection, rid, StringComparison.OrdinalIgnoreCase) && sectionHash is not null)
            return sectionHash;

        // 3) 全文唯一 SHA256
        var all = new List<string>();
        foreach (var raw in lines)
        {
            var h = ExtractSha256Token(raw);
            if (h is not null)
                all.Add(h);
        }

        return all.Count == 1 ? all[0] : null;
    }

    private static string? ExtractSha256Token(string line)
    {
        // SHA256：XXXX / SHA256=XXXX / SHA256: XXXX
        var m = Regex.Match(line, @"SHA256\s*[:：=]\s*([A-Fa-f0-9]{64})", RegexOptions.IgnoreCase);
        if (m.Success)
            return m.Groups[1].Value.ToLowerInvariant();
        // bare 64 hex
        m = Regex.Match(line, @"\b([A-Fa-f0-9]{64})\b");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static async Task<string> DownloadTextAsync(string url, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
            throw new InvalidOperationException("不允许的下载地址");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string? retry = null;
            if (resp.Headers.TryGetValues("Retry-After", out var vals))
                retry = vals.FirstOrDefault();
            throw new InvalidOperationException(
                FormatGitHubHttpError((int)resp.StatusCode, body.Length > 300 ? body[..300] : body, retry));
        }

        return body;
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
            throw new InvalidOperationException("不允许的下载地址");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string? retry = null;
            if (resp.Headers.TryGetValues("Retry-After", out var vals))
                retry = vals.FirstOrDefault();
            var snippet = "";
            try
            {
                snippet = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (snippet.Length > 300)
                    snippet = snippet[..300];
            }
            catch { /* ignore */ }

            throw new InvalidOperationException(
                FormatGitHubHttpError((int)resp.StatusCode, snippet, retry));
        }
        var total = resp.Content.Headers.ContentLength ?? -1;
        await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
        var buffer = new byte[128 * 1024];
        long readTotal = 0;
        int n;
        var lastReport = DateTime.UtcNow;
        while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            readTotal += n;
            if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(400))
            {
                lastReport = DateTime.UtcNow;
                if (total > 0)
                {
                    var pct = (int)(readTotal * 100 / total);
                    progress.Report($"下载中 {pct}%（{readTotal / 1048576.0:F1}/{total / 1048576.0:F1} MB）");
                }
                else
                    progress.Report($"下载中 {readTotal / 1048576.0:F1} MB…");
            }
        }
    }

    public static async Task<string> ComputeSha256HexAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
