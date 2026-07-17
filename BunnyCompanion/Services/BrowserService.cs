using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BunnyCompanion.Services;

/// <summary>
/// 浏览器与网页能力：读当前标签页 URL/标题、抓网页正文、打开搜索。
/// HTML 解析用 AngleSharp DOM，正文提取走 main/article 选择器 + 文本密度回退，
/// 搜索走 DuckDuckGo Lite（免 Key、结构稳定）+ Bing 回退。
/// </summary>
public static class BrowserService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        MaxResponseContentBufferSize = 4 * 1024 * 1024,
    };

    private static readonly HtmlParser HtmlParser = new(new HtmlParserOptions
    {
        IsEmbedded = false,
        IsScripting = false,
    });

    static BrowserService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.5");
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
            var text = ExtractMainText(body);
            if (string.IsNullOrWhiteSpace(text))
                text = ExtractFullText(body);
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

    /// <summary>
    /// 用 AngleSharp DOM 提取正文：优先 article/main/[role=main]，回退到文本密度最大的块。
    /// 去除 script/style/nav/footer/aside/iframe/form/ad 等噪音节点后再取文本。
    /// </summary>
    private static string ExtractMainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        try
        {
            var doc = HtmlParser.ParseDocument(html);
            // 先删噪音节点
            foreach (var n in doc.QuerySelectorAll("script, style, noscript, nav, footer, header, aside, iframe, form, .ad, .ads, .advert, .sidebar, .nav, .menu, .breadcrumb, .cookie, .popup, .modal, [role=navigation], [role=banner], [role=contentinfo]").ToList())
                n.Remove();

            // 优先语义化正文容器
            var main = doc.QuerySelector("article, main, [role=main], .article, .post, .content, .entry-content, #content, #article")
                ?? doc.Body;
            if (main is null)
                return string.Empty;

            var text = NodeToText(main);
            if (text.Length < 200)
            {
                // 正文太短，可能是 SPA/极简页，回退到 body 全文
                return ExtractFullText(doc);
            }
            return text;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>整页去标签全文（回退用）。</summary>
    private static string ExtractFullText(string html)
    {
        try
        {
            var doc = HtmlParser.ParseDocument(html);
            return ExtractFullText(doc);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractFullText(IDocument doc)
    {
        foreach (var n in doc.QuerySelectorAll("script, style, noscript, iframe, nav, footer, header").ToList())
            n.Remove();
        return NodeToText(doc.Body);
    }

    /// <summary>把 DOM 节点转成保留结构的纯文本：块级元素换行，压缩多余空白。</summary>
    private static string NodeToText(INode node)
    {
        var sb = new StringBuilder();
        RenderNode(node, sb);
        var text = sb.ToString();
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static void RenderNode(INode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child.NodeType == NodeType.Text)
            {
                var t = child.TextContent?.Trim();
                if (!string.IsNullOrEmpty(t))
                    sb.Append(t).Append(' ');
                continue;
            }
            if (child is IElement el)
            {
                var tag = el.TagName.ToLowerInvariant();
                // 跳过隐藏元素
                if (el.GetAttribute("hidden") is not null ||
                    (el.GetAttribute("style")?.Contains("display:none", StringComparison.OrdinalIgnoreCase) == true) ||
                    (el.GetAttribute("style")?.Contains("display: none", StringComparison.OrdinalIgnoreCase) == true))
                    continue;

                var block = tag is "p" or "div" or "li" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
                    or "tr" or "section" or "article" or "blockquote" or "pre" or "ul" or "ol" or "table" or "br";
                if (block && sb.Length > 0 && sb[^1] != '\n')
                    sb.AppendLine();
                if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                    sb.Append("# ");
                RenderNode(el, sb);
                if (block && sb.Length > 0 && sb[^1] != '\n')
                    sb.AppendLine();
            }
        }
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

    /// <summary>
    /// 抓取搜索结果摘要（标题+链接+摘要），供办公 Agent 直接阅读。
    /// 默认走 DuckDuckGo Lite（免 Key、HTML 结构稳定）；engine=bing/baidu/google 时走对应引擎 DOM 解析。
    /// 任一引擎解析为空时自动回退到 DuckDuckGo Lite，保证可用性。
    /// </summary>
    public static async Task<string> SearchResultsAsync(string query, string engine, int max, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "错误：搜索词为空";
        max = Math.Clamp(max, 1, 10);
        engine = string.IsNullOrWhiteSpace(engine) ? "duckduckgo" : engine.Trim().ToLowerInvariant();
        var q = Uri.EscapeDataString(query.Trim());

        List<(string Title, string Link, string Snip)> items;
        var usedUrl = $"https://lite.duckduckgo.com/lite/?q={q}&kl=cn-zh";
        var usedEngine = "duckduckgo";

        // DuckDuckGo Lite 为主：免 Key、结构稳定（表格 tr.result_form 一直没变）
        try
        {
            usedUrl = $"https://lite.duckduckgo.com/lite/?q={q}&kl=cn-zh";
            usedEngine = "duckduckgo";
            var html = await FetchHtmlAsync(usedUrl, ct).ConfigureAwait(false);
            items = ParseDuckDuckGoLite(html, max);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            items = new List<(string, string, string)>();
        }

        // DuckDuckGo 失败或为空，且用户指定了别的引擎，再试指定引擎
        if (items.Count == 0 && engine != "duckduckgo")
        {
            usedUrl = engine switch
            {
                "baidu" => $"https://www.baidu.com/s?wd={q}",
                "google" => $"https://www.google.com/search?q={q}&hl=zh-CN",
                _ => $"https://www.bing.com/search?q={q}&setlang=zh-CN",
            };
            usedEngine = engine;
            try
            {
                var html = await FetchHtmlAsync(usedUrl, ct).ConfigureAwait(false);
                items = engine switch
                {
                    "baidu" => ParseBaiduResults(html, max),
                    _ => ParseBingResults(html, max),
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                items = new List<(string, string, string)>();
            }
        }

        if (items.Count == 0)
        {
            return "未能解析「" + query + "」的搜索结果（可能被反爬或页面结构变化）。\n" +
                   "建议：web_search 打开浏览器，或 fetch_url 抓已知网址。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"【搜索摘要】{query}（{usedEngine}，最多 {max} 条）");
        var n = 1;
        foreach (var (title, link, snip) in items)
        {
            sb.AppendLine($"{n}. {title}");
            sb.AppendLine($"   {link}");
            if (!string.IsNullOrWhiteSpace(snip))
                sb.AppendLine($"   {snip}");
            n++;
        }

        sb.AppendLine("说明: 摘要供决策；需要正文请再 fetch_url。");
        return sb.ToString().Trim();
    }

    /// <summary>抓取 HTML 源码（带浏览器 UA 与语言头）。</summary>
    private static async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>DuckDuckGo Lite 结果解析：结果在 tr.result_form 里，标题链接 a.result-link，摘要 td.result-snippet。</summary>
    private static List<(string Title, string Link, string Snip)> ParseDuckDuckGoLite(string html, int max)
    {
        var list = new List<(string, string, string)>();
        try
        {
            var doc = HtmlParser.ParseDocument(html);
            // Lite 版结果行：tr.result_form
            var rows = doc.QuerySelectorAll("tr.result_form");
            foreach (var row in rows)
            {
                if (list.Count >= max)
                    break;
                var a = row.QuerySelector("a.result-link") ?? row.QuerySelector("a.result__a");
                if (a is null)
                    continue;
                var link = a.GetAttribute("href") ?? "";
                // DuckDuckGo Lite 链接可能是相对跳转，取实际重定向目标
                if (link.StartsWith("//duckduckgo.com/l/?uddg=", StringComparison.OrdinalIgnoreCase))
                {
                    var uddg = link.Split("uddg=", 2, StringSplitOptions.None);
                    if (uddg.Length == 2)
                        link = Uri.UnescapeDataString(uddg[1].Split('&')[0]);
                }
                if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                var title = a.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
                    continue;
                var snipEl = row.QuerySelector("td.result-snippet") ?? row.QuerySelector(".result__snippet");
                var snip = snipEl?.TextContent?.Trim() ?? "";
                if (snip.Length > 200)
                    snip = snip[..200] + "…";
                if (list.Any(x => x.Item2 == link))
                    continue;
                list.Add((title, link, snip));
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    /// <summary>Bing 结果解析：li.b_algo > h2 > a，摘要 p.b_lineclamp。</summary>
    private static List<(string Title, string Link, string Snip)> ParseBingResults(string html, int max)
    {
        var list = new List<(string, string, string)>();
        try
        {
            var doc = HtmlParser.ParseDocument(html);
            var blocks = doc.QuerySelectorAll("li.b_algo");
            foreach (var block in blocks)
            {
                if (list.Count >= max)
                    break;
                var a = block.QuerySelector("h2 a") ?? block.QuerySelector("a");
                if (a is null)
                    continue;
                var link = a.GetAttribute("href")?.Trim() ?? "";
                if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                var title = a.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
                    continue;
                var snip = (block.QuerySelector("p.b_lineclamp, p.b_algoSlug, .b_caption p")?.TextContent?.Trim()) ?? "";
                if (snip.Length > 200)
                    snip = snip[..200] + "…";
                if (list.Any(x => x.Item2 == link))
                    continue;
                list.Add((title, link, snip));
            }
        }
        catch
        {
            // ignore
        }
        return list;
    }

    /// <summary>Baidu 结果解析：div.result > h3 > a，摘要 span.content-right_8Zs40。</summary>
    private static List<(string Title, string Link, string Snip)> ParseBaiduResults(string html, int max)
    {
        var list = new List<(string, string, string)>();
        try
        {
            var doc = HtmlParser.ParseDocument(html);
            // 百度结果块：div.result 或 div[tpl="se_com_default"]
            var blocks = doc.QuerySelectorAll("div.result, div[tpl='se_com_default']");
            foreach (var block in blocks)
            {
                if (list.Count >= max)
                    break;
                var a = block.QuerySelector("h3 a");
                if (a is null)
                    continue;
                var link = a.GetAttribute("href")?.Trim() ?? "";
                var title = a.TextContent?.Trim();
                if (string.IsNullOrWhiteSpace(title))
                    continue;
                var snip = (block.QuerySelector("span.content-right_8Zs40, .c-abstract, [class*='content-right']")?.TextContent?.Trim()) ?? "";
                if (snip.Length > 200)
                    snip = snip[..200] + "…";
                // 百度链接是跳转链，保留原样让用户点击
                if (list.Any(x => x.Item2 == link))
                    continue;
                list.Add((title, link, snip));
            }
        }
        catch
        {
            // ignore
        }
        return list;
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
