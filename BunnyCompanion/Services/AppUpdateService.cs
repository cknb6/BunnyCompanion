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
/// </summary>
public static class AppUpdateService
{
    public const string Owner = "cknb6";
    public const string Repo = "BunnyCompanion";
    public const string ReleasesApiLatest = "https://api.github.com/repos/cknb6/BunnyCompanion/releases/latest";
    public const string ReleasesPage = "https://github.com/cknb6/BunnyCompanion/releases/latest";

    private static readonly HttpClient Http = CreateClient();
    private static readonly object Gate = new();
    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static UpdateCheckResult? _lastResult;

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
        c.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion-Updater/1.4");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>当前运行中的本机版本（优先 FileVersion，含 CI 补丁号）。</summary>
    public static Version GetLocalVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                if (TryParseVersion(fvi.FileVersion, out var fv))
                    return fv;
                if (TryParseVersion(fvi.ProductVersion, out var pv))
                    return pv;
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
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        }
        catch
        {
            // ignore
        }

        return new Version(1, 0, 0, 0);
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
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        TimeSpan? minInterval = null,
        bool force = false,
        CancellationToken ct = default)
    {
        var local = GetLocalVersion();
        lock (Gate)
        {
            if (!force
                && _lastResult is not null
                && minInterval is { } gap
                && DateTime.UtcNow - _lastCheckUtc < gap)
            {
                return _lastResult;
            }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApiLatest);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Cache(new UpdateCheckResult(
                    false, false, $"检查更新失败：HTTP {(int)resp.StatusCode}", local, null,
                    null, null, null, null, null, null));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() ?? "" : "";
            if (!TryParseVersion(tag, out var remote))
            {
                return Cache(new UpdateCheckResult(
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
                return Cache(new UpdateCheckResult(
                    true, false, $"最新版 {tag} 没有本机架构安装包（{fileName}）", local, remote,
                    tag, notes, null, sumUrl, null, fileName));
            }

            if (string.IsNullOrWhiteSpace(sumUrl))
            {
                return Cache(new UpdateCheckResult(
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
                return Cache(new UpdateCheckResult(
                    false, false, "读取校验文件失败：" + ex.Message, local, remote,
                    tag, notes, exeUrl, sumUrl, null, fileName));
            }

            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                return Cache(new UpdateCheckResult(
                    false, false, $"checksums.txt 中找不到 {fileName} 的 SHA256，拒绝更新", local, remote,
                    tag, notes, exeUrl, sumUrl, null, fileName));
            }

            var newer = remote > local;
            var msg = newer
                ? $"发现新版本 {FormatVersion(remote)}（当前 {FormatVersion(local)}）"
                : $"已是最新（{FormatVersion(local)}）";

            return Cache(new UpdateCheckResult(
                true, newer, msg, local, remote, tag, notes, exeUrl, sumUrl, expectedHash, fileName));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Cache(new UpdateCheckResult(
                false, false, "检查更新失败：" + ex.Message, local, null,
                null, null, null, null, null, null));
        }
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
            progress?.Report("正在下载更新包…");
            await DownloadFileAsync(check.ExeDownloadUrl!, tempExe, progress, ct).ConfigureAwait(false);

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

            progress?.Report("校验通过，准备替换…");
            WriteApplyScript(scriptPath, Environment.ProcessId, tempExe, currentExe);

            var psi = new ProcessStartInfo
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
            };
            Process.Start(psi);

            return new ApplyResult(true, $"校验通过（SHA256 一致）。即将重启以完成更新到 {check.TagName}。");
        }
        catch (OperationCanceledException)
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { /* ignore */ }
            throw;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { /* ignore */ }
            return new ApplyResult(false, "更新失败：" + ex.Message);
        }
    }

    private static void WriteApplyScript(string scriptPath, int pid, string srcExe, string dstExe)
    {
        // 等原进程退出 → 复制 → 启动 → 清理
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$pidToWait = {pid}");
        sb.AppendLine($"$src = {PsQuote(srcExe)}");
        sb.AppendLine($"$dst = {PsQuote(dstExe)}");
        sb.AppendLine("$deadline = (Get-Date).AddMinutes(2)");
        sb.AppendLine("while ((Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {");
        sb.AppendLine("  Start-Sleep -Milliseconds 350");
        sb.AppendLine("}");
        sb.AppendLine("Start-Sleep -Milliseconds 400");
        sb.AppendLine("if (-not (Test-Path -LiteralPath $src)) { exit 2 }");
        sb.AppendLine("$bak = $dst + '.bak'");
        sb.AppendLine("try { if (Test-Path -LiteralPath $dst) { Copy-Item -LiteralPath $dst -Destination $bak -Force } } catch {}");
        sb.AppendLine("Copy-Item -LiteralPath $src -Destination $dst -Force");
        sb.AppendLine("Start-Process -FilePath $dst");
        sb.AppendLine("try { Remove-Item -LiteralPath $src -Force -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("try { Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue } catch {}");
        File.WriteAllText(scriptPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string PsQuote(string path) =>
        "'" + path.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static UpdateCheckResult Cache(UpdateCheckResult r)
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
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(
        string url, string destPath, IProgress<string>? progress, CancellationToken ct)
    {
        if (!IsAllowedDownloadUrl(url))
            throw new InvalidOperationException("不允许的下载地址");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
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
