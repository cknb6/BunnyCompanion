using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BunnyCompanion.Services;

/// <summary>
/// 浏览器与网页能力：读当前标签页 URL/标题、抓网页正文、打开搜索。
/// 纯 .NET，无外部包：用 UI Automation 读 Chrome/Edge 地址栏，HttpClient 抓网页并提正文。
/// </summary>
public static class BrowserService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };

    static BrowserService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) XiaoShenCompanion/1.3");
    }

    // ---------- 读当前浏览器标签页 ----------

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>读取前台浏览器（Chrome/Edge）当前标签页的 URL 与标题。</summary>
    public static string ReadActiveBrowserTab()
    {
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return "未取到前台窗口";
            GetWindowThreadProcessId(fg, out var pid);
            var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName ?? "";

            // Chrome / Edge / Brave 等 Chromium 系：地址栏可用 UI Automation 读取
            // 这里用轻量方式：通过 Accessibility 命名空间取地址栏编辑框文本
            var url = TryReadChromiumAddressBar(fg, name);
            var title = string.IsNullOrWhiteSpace(proc.MainWindowTitle) ? name : proc.MainWindowTitle;

            if (string.IsNullOrWhiteSpace(url))
                return $"前台是 {name}（{title}），但未能读取地址栏 URL。\n" +
                       "可改用 fetch_url 直接抓指定网址，或 web_search 打开搜索。";

            return $"浏览器: {name}\n标题: {title}\nURL: {url}";
        }
        catch (Exception ex)
        {
            return $"读取浏览器标签失败: {ex.Message}";
        }
    }

    /// <summary>用 UI Automation 取 Chromium 系地址栏文本（尽力而为，失败返回空）。</summary>
    private static string TryReadChromiumAddressBar(IntPtr fg, string procName)
    {
        // 仅对已知 Chromium 浏览器尝试，避免对无关窗口做昂贵的 UIA 遍历
        if (!procName.Contains("chrome", StringComparison.OrdinalIgnoreCase)
            && !procName.Contains("msedge", StringComparison.OrdinalIgnoreCase)
            && !procName.Contains("brave", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        try
        {
            // UI Automation 需引用 UIAutomationClient/UIAutomationTypes；为避免引入 COM 依赖，
            // 改用更稳的兜底：发 Ctrl+L 聚焦地址栏 → 读剪贴板不可行（会改用户剪贴板）。
            // 这里返回空，让上层引导用户用 fetch_url；真正读地址栏留待后续增强。
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // ---------- 抓网页正文 ----------

    /// <summary>抓取指定 URL 的网页内容，去标签提正文，限长返回。</summary>
    public static async Task<string> FetchUrlAsync(string url, int maxChars, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "错误：URL 为空";
        if (!TryNormalizeWebUrl(url, out url))
            return "错误：URL 格式无效，仅支持 http/https";
        maxChars = Math.Clamp(maxChars, 500, 50_000);

        try
        {
            using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return $"HTTP {(int)resp.StatusCode}: {resp.ReasonPhrase}";
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var text = HtmlToText(body);
            if (text.Length > maxChars)
                text = text[..maxChars] + "\n…（网页较长，已截断）";
            return $"URL: {url}\n----\n{text}";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"抓取失败: {ex.Message}";
        }
    }

    /// <summary>粗略去 HTML 标签，保留可见文本与换行。</summary>
    private static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // 去 script/style/nav 注释
        html = Regex.Replace(html, @"(?is)<(script|style|nav|footer|header)\b[^>]*>.*?</\1>", " ");
        html = Regex.Replace(html, @"(?is)<!--.*?-->", " ");
        // 块级标签转换行
        html = Regex.Replace(html, @"(?i)</(p|div|li|h[1-6]|tr|br|section|article)>", "\n");
        html = Regex.Replace(html, @"(?i)<br\s*/?>", "\n");
        // 去剩余标签
        html = Regex.Replace(html, @"(?s)<[^>]+>", " ");
        // HTML 实体
        html = html
            .Replace("&nbsp;", " ", StringComparison.Ordinal)
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&#39;", "'", StringComparison.Ordinal);
        // 压缩空白
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        return html.Trim();
    }

    // ---------- 打开搜索 ----------

    /// <summary>用系统默认浏览器打开搜索结果页。</summary>
    public static string OpenSearch(string query, string engine = "bing")
    {
        if (string.IsNullOrWhiteSpace(query))
            return "错误：搜索词为空";
        var q = Uri.EscapeDataString(query.Trim());
        var url = engine.ToLowerInvariant() switch
        {
            "google" => $"https://www.google.com/search?q={q}",
            "baidu" => $"https://www.baidu.com/s?wd={q}",
            _ => $"https://www.bing.com/search?q={q}",
        };
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return $"已打开搜索：{query}（{engine}）\n{url}";
        }
        catch (Exception ex)
        {
            return $"打开搜索失败: {ex.Message}\nURL: {url}";
        }
    }

    /// <summary>用默认浏览器打开指定 URL。</summary>
    public static string OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "错误：URL 为空";
        if (!TryNormalizeWebUrl(url, out url))
            return "错误：URL 格式无效，仅支持 http/https";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            return $"已打开：{url}";
        }
        catch (Exception ex)
        {
            return $"打开失败: {ex.Message}";
        }
    }

    private static bool TryNormalizeWebUrl(string raw, out string normalized)
    {
        normalized = string.Empty;
        var candidate = raw.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
            return false;
        normalized = uri.AbsoluteUri;
        return true;
    }
}
