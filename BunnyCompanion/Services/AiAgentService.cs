using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using BunnyCompanion.Models;

namespace BunnyCompanion.Services;

public enum ChatAttachmentKind
{
    Image,
    Text,
    Other,
}

public sealed record ChatAttachment(
    string FileName,
    ChatAttachmentKind Kind,
    string? MimeType,
    string? TextContent,
    byte[]? ImageBytes);

public sealed record AgentResult(
    string Text,
    string ActionKey,
    int AffectionGain,
    string Provider,
    bool UsedDesktopImage,
    IReadOnlyList<string>? ToolTrace = null);

/// <summary>
/// 全能桌宠 Agent：阶跃 step-3.7-flash（主·工具）→ OpenRouter 免费（兜底）→ 本地关键词。
/// 支持 Windows 本机工具循环（定位/天气/文件/命令等）。
/// </summary>
public sealed class AiAgentService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly List<ChatTurn> _history = [];
    private readonly object _gate = new();
    private readonly CompanionMemoryService _memory;
    private readonly LocalAgentMdStore _agentMd;

    private sealed record ChatTurn(string Role, string Text);

    public AiAgentService(CompanionMemoryService? memory = null, LocalAgentMdStore? agentMd = null)
    {
        _memory = memory ?? CompanionRuntime.Memory;
        CompanionRuntime.Memory = _memory;
        _agentMd = agentMd ?? CompanionRuntime.AgentMd;
        CompanionRuntime.AgentMd = _agentMd;
    }

    /// <summary>长期记忆服务（人物/偏好），供气泡与自检使用。</summary>
    public CompanionMemoryService Memory => _memory;

    /// <summary>本地 agent.md（对话摘要压缩）。</summary>
    public LocalAgentMdStore AgentMd => _agentMd;

    public void ClearHistory()
    {
        lock (_gate)
            _history.Clear();
    }

    public async Task<AgentResult> ChatAsync(
        string userText,
        PetSettings settings,
        bool includeDesktop,
        Window? hostWindow,
        IReadOnlyList<ChatAttachment>? attachments = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var text = (userText ?? string.Empty).Trim();
        var attachList = attachments?.ToList() ?? [];
        var hasImageAttach = attachList.Any(a => a.Kind == ChatAttachmentKind.Image && a.ImageBytes is { Length: > 0 });
        var hasTextAttach = attachList.Any(a => a.Kind == ChatAttachmentKind.Text && !string.IsNullOrWhiteSpace(a.TextContent));

        if (text.Length == 0)
        {
            if (includeDesktop)
                text = "帮我看一下桌面，说说屏幕上有什么，给点贴心建议就好。";
            else if (hasImageAttach)
                text = "看看我发的图，跟我说你看到了什么。";
            else if (hasTextAttach)
                text = "看看我发的文件，帮我读一下并说说重点。";
            else
                text = "在吗";
        }

        if (text.Length > 4000)
            text = text[..4000];

        var composedUser = ComposeUserMessage(text, attachList);
        var wantDesktop = includeDesktop || LooksLikeDesktopRequest(text);
        byte[]? imageBytes = null;
        if (wantDesktop)
        {
            progress?.Report("正在截取桌面…");
            var capture = await Task.Run(() => DesktopCaptureService.CaptureNearWindow(hostWindow), cancellationToken)
                .ConfigureAwait(false);
            imageBytes = capture?.JpegBytes;
        }

        if (hasImageAttach)
            imageBytes = attachList.First(a => a.Kind == ChatAttachmentKind.Image && a.ImageBytes is { Length: > 0 }).ImageBytes;

        lock (_gate)
        {
            _history.Add(new ChatTurn("user", composedUser));
            TrimHistoryUnlocked();
        }

        List<ChatTurn> historySnapshot;
        lock (_gate)
            historySnapshot = _history.ToList();

        // 先沉淀本轮用户话 → 人物/偏好记忆，再拼系统提示
        try
        {
            _memory.IngestUserUtterance(text);
        }
        catch
        {
            // 记忆失败不阻断对话
        }

        var systemPrompt = AgentSystemPrompt.Build(settings);
        var memoryBlock = _memory.FormatForSystemPrompt();
        if (!string.IsNullOrWhiteSpace(memoryBlock))
            systemPrompt += "\n\n" + memoryBlock;
        // 本地 agent.md：滚动摘要 + 近期压缩对话（长期记忆主载体之一）
        try
        {
            var agentMdBlock = _agentMd.FormatForSystemPrompt(maxChars: 5500);
            if (!string.IsNullOrWhiteSpace(agentMdBlock) && agentMdBlock.Length > 80)
                systemPrompt += "\n\n" + agentMdBlock;
        }
        catch { /* ignore */ }

        var toolTrace = new List<string>();

        // 本地意图预取：定位/天气在模型不会 tool-call 时仍能答对
        var prefetch = await MaybePrefetchLocalFactsAsync(text, progress, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(prefetch))
        {
            systemPrompt += "\n\n# 本回合已预取的真实数据（请直接采用，勿编造）\n" + prefetch;
            toolTrace.Add("prefetch");
        }

        // 1) 阶跃 + tools
        progress?.Report("小申 Agent 思考中…");
        var step = await TryAgentLoopAsync(
            providerLabel: "阶跃·3.7",
            baseUrl: AiConfig.StepBaseUrl,
            apiKey: AiConfig.StepApiKey,
            model: AiConfig.StepModel,
            openRouter: false,
            systemPrompt: systemPrompt,
            history: historySnapshot,
            imageBytes: imageBytes,
            maxTokens: 2800,
            enableTools: true,
            progress: progress,
            toolTrace: toolTrace,
            extra: node => node["reasoning_effort"] = AiConfig.StepEffort,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (step is { } s)
            return Finalize((s.Text, InferAction(s.Text)), settings, imageBytes is not null, s.Provider, toolTrace, userText: text);

        // 2) OpenRouter 免费 + tools（部分模型可能忽略 tools）
        foreach (var model in imageBytes is not null
                     ? AiConfig.OpenRouterFreeVisionModels
                     : AiConfig.OpenRouterFreeTextModels)
        {
            progress?.Report($"备用通道 {ShortModel(model)}…");
            var hit = await TryAgentLoopAsync(
                providerLabel: $"OpenRouter·{ShortModel(model)}",
                baseUrl: AiConfig.OpenRouterBaseUrl,
                apiKey: AiConfig.OpenRouterApiKey,
                model: model,
                openRouter: true,
                systemPrompt: systemPrompt,
                history: historySnapshot,
                imageBytes: imageBytes,
                maxTokens: 2200,
                enableTools: true,
                progress: progress,
                toolTrace: toolTrace,
                extra: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (hit is { } h)
                return Finalize((h.Text, InferAction(h.Text)), settings, imageBytes is not null, h.Provider, toolTrace, userText: text);
        }

        // 3) 纯文本再试阶跃（无 tools）
        var stepPlain = await TryAgentLoopAsync(
            providerLabel: "阶跃·3.7",
            baseUrl: AiConfig.StepBaseUrl,
            apiKey: AiConfig.StepApiKey,
            model: AiConfig.StepModel,
            openRouter: false,
            systemPrompt: systemPrompt + "\n" + AgentSystemPrompt.BuildTextOnlyAddon(),
            history: historySnapshot,
            imageBytes: imageBytes,
            maxTokens: 2200,
            enableTools: false,
            progress: progress,
            toolTrace: toolTrace,
            extra: node => node["reasoning_effort"] = AiConfig.StepEffort,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (stepPlain is { } sp)
            return Finalize((sp.Text, InferAction(sp.Text)), settings, imageBytes is not null, sp.Provider, toolTrace, userText: text);

        // 4) 本地关键词 + 预取数据拼装
        var offline = ChatReplyService.Reply(text, settings, offlineMode: true, desktopRequested: wantDesktop && imageBytes is null);
        var offlineText = offline.Text;
        if (!string.IsNullOrWhiteSpace(prefetch))
            offlineText = $"{offlineText}\n\n——\n{FormatPrefetchForUser(prefetch)}";
        else if (hasTextAttach || hasImageAttach)
            offlineText = $"{offlineText}\n（在线模型暂时忙，附件我记下了。）";
        else if (wantDesktop)
            offlineText = $"{offlineText}\n（在线模型暂时不可用，先本地陪你。）";
        else
            offlineText = $"{offlineText}\n（网络不太顺，我先用本地模式陪你。）";

        return Finalize((offlineText, offline.ActionKey), settings, false, "本地", toolTrace, userText: text);
    }

    private sealed record LoopHit(string Text, string Provider);

    private async Task<LoopHit?> TryAgentLoopAsync(
        string providerLabel,
        string baseUrl,
        string apiKey,
        string model,
        bool openRouter,
        string systemPrompt,
        IReadOnlyList<ChatTurn> history,
        byte[]? imageBytes,
        int maxTokens,
        bool enableTools,
        IProgress<string>? progress,
        List<string> toolTrace,
        Action<JsonObject>? extra,
        CancellationToken cancellationToken)
    {
        try
        {
            // 可变消息列表（含 tool 回合）
            var messages = BuildInitialMessages(systemPrompt, history, imageBytes);
            var tools = enableTools ? WindowsAgentToolkit.BuildToolDefinitions() : null;

            for (var round = 0; round < AiConfig.MaxToolRounds; round++)
            {
                var payload = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = messages,
                    ["temperature"] = 0.7,
                    ["max_tokens"] = maxTokens,
                };
                if (tools is not null)
                {
                    payload["tools"] = tools.DeepClone();
                    payload["tool_choice"] = "auto";
                }

                extra?.Invoke(payload);

                var json = await PostChatAsync(
                    $"{baseUrl.TrimEnd('/')}/chat/completions",
                    apiKey,
                    payload,
                    cancellationToken,
                    openRouter).ConfigureAwait(false);

                var parsed = ParseAssistantMessage(json);
                if (parsed is null)
                    return null;

                // 有 tool_calls：执行并回灌
                if (enableTools && parsed.ToolCalls is { Count: > 0 })
                {
                    // 把 assistant tool_calls 消息追加
                    messages.Add(parsed.RawAssistantMessage);

                    foreach (var call in parsed.ToolCalls)
                    {
                        progress?.Report($"执行工具：{call.Name}…");
                        toolTrace.Add(call.Name);
                        JsonObject? argsObj = null;
                        try
                        {
                            argsObj = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                                ? new JsonObject()
                                : JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? new JsonObject();
                        }
                        catch
                        {
                            argsObj = new JsonObject();
                        }

                        var result = await WindowsAgentToolkit.ExecuteAsync(call.Name, argsObj, cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Length > AiConfig.MaxToolResultChars)
                            result = result[..AiConfig.MaxToolResultChars] + "\n…(工具输出截断)";

                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call.Id,
                            ["content"] = result,
                        });
                    }

                    continue; // 下一轮让模型总结
                }

                // 最终文本
                if (!string.IsNullOrWhiteSpace(parsed.Content))
                    return new LoopHit(parsed.Content, providerLabel);

                // 有些模型 content 空但有 reasoning —— 视为失败
                return null;
            }

            return new LoopHit("工具步骤有点多，我先停一下～你再说具体一点目标，我继续帮你弄。", providerLabel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ToolCall(string Id, string Name, string ArgumentsJson);

    private sealed record ParsedAssistant(
        string? Content,
        IReadOnlyList<ToolCall>? ToolCalls,
        JsonObject RawAssistantMessage);

    private static ParsedAssistant? ParseAssistantMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var message = choices[0].GetProperty("message");
            // 重建为 JsonObject 便于回灌
            var raw = JsonNode.Parse(message.GetRawText()) as JsonObject ?? new JsonObject { ["role"] = "assistant" };
            if (!raw.ContainsKey("role"))
                raw["role"] = "assistant";

            string? content = null;
            if (message.TryGetProperty("content", out var contentElement))
            {
                if (contentElement.ValueKind == JsonValueKind.String)
                    content = contentElement.GetString();
                else if (contentElement.ValueKind == JsonValueKind.Array)
                {
                    var sb = new StringBuilder();
                    foreach (var part in contentElement.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var t))
                            sb.Append(t.GetString());
                    }

                    content = sb.ToString();
                }
            }

            List<ToolCall>? calls = null;
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
            {
                calls = [];
                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
                    var name = "";
                    var args = "{}";
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        args = fn.TryGetProperty("arguments", out var a)
                            ? (a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText())
                            : "{}";
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                        calls.Add(new ToolCall(id, name, args));
                }
            }

            if (calls is { Count: 0 })
                calls = null;

            return new ParsedAssistant(content?.Trim(), calls, raw);
        }
        catch
        {
            return null;
        }
    }

    private static JsonArray BuildInitialMessages(string systemPrompt, IReadOnlyList<ChatTurn> history, byte[]? imageBytes)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = systemPrompt,
            },
        };

        foreach (var turn in history.Take(Math.Max(0, history.Count - 1)))
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Text,
            });
        }

        var last = history.Count > 0 ? history[^1] : new ChatTurn("user", "在吗");
        if (imageBytes is { Length: > 0 } && last.Role == "user")
        {
            var dataUrl = LooksLikePng(imageBytes) && !LooksLikeJpeg(imageBytes)
                ? $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}"
                : $"data:image/jpeg;base64,{Convert.ToBase64String(imageBytes)}";
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = last.Text },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = dataUrl },
                    },
                },
            });
        }
        else
        {
            messages.Add(new JsonObject
            {
                ["role"] = last.Role,
                ["content"] = last.Text,
            });
        }

        return messages;
    }

    /// <summary>
    /// 对定位/天气类问题预取真实数据，避免模型不会 tool-call 时瞎编。
    /// </summary>
    private static async Task<string?> MaybePrefetchLocalFactsAsync(
        string text,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var wantLoc = ContainsAny(text, "我在哪", "在哪里", "定位", "什么地方", "哪个城市", "where am i", "我的位置");
        var wantWeather = ContainsAny(text, "天气", "气温", "冷不冷", "热不热", "下雨", "带伞", "weather",
            "高温", "降水", "预警", "出门", "加衣服", "降温", "紫外线");
        var wantZodiac = ContainsAny(text, "星座", "运势", "白羊", "金牛", "双子", "巨蟹", "狮子", "处女",
            "天秤", "天蝎", "射手", "摩羯", "水瓶", "双鱼");
        var wantDaily = ContainsAny(text, "今日运势", "陪伴卡", "今日卡片", "穿搭建议");
        if (!wantLoc && !wantWeather && !wantZodiac && !wantDaily)
            return null;

        var sb = new StringBuilder();
        if (wantLoc || wantWeather)
        {
            progress?.Report("正在定位…");
            var loc = await WindowsAgentToolkit.ExecuteAsync("get_location", new JsonObject(), ct).ConfigureAwait(false);
            sb.AppendLine("## get_location");
            sb.AppendLine(loc);
        }

        if (wantWeather)
        {
            progress?.Report("正在查天气…");
            var weather = await WindowsAgentToolkit.ExecuteAsync("get_weather", new JsonObject(), ct).ConfigureAwait(false);
            sb.AppendLine("## get_weather");
            sb.AppendLine(weather);
        }

        if (wantZodiac)
        {
            progress?.Report("正在算星座…");
            var q = text;
            var z = ZodiacService.Analyze(q, "宝宝");
            sb.AppendLine("## zodiac_analyze");
            sb.AppendLine(z);
        }

        if (wantDaily)
        {
            sb.AppendLine("## daily_card");
            sb.AppendLine(DailyCompanion.BuildDailyCard("宝宝"));
        }

        return sb.ToString().Trim();
    }

    private static string FormatPrefetchForUser(string prefetch)
    {
        // 去掉 markdown 标题，变口语一点
        return prefetch
            .Replace("## get_location", "【定位】", StringComparison.Ordinal)
            .Replace("## get_weather", "【天气】", StringComparison.Ordinal)
            .Trim();
    }

    private static string ComposeUserMessage(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
            return text;

        var sb = new StringBuilder();
        sb.AppendLine(text);
        sb.AppendLine();

        var imageNames = attachments.Where(a => a.Kind == ChatAttachmentKind.Image).Select(a => a.FileName).ToList();
        if (imageNames.Count > 0)
            sb.AppendLine($"【用户上传了图片】{string.Join("、", imageNames)}（图已附在消息中，请仔细看）");

        foreach (var a in attachments.Where(x => x.Kind == ChatAttachmentKind.Text))
        {
            var body = a.TextContent ?? string.Empty;
            if (body.Length > AiConfig.MaxAttachmentTextChars)
                body = body[..AiConfig.MaxAttachmentTextChars] + "\n…（后文过长已截断）";
            sb.AppendLine();
            sb.AppendLine($"【附件文件：{a.FileName}】");
            sb.AppendLine("```");
            sb.AppendLine(body);
            sb.AppendLine("```");
            sb.AppendLine("请基于上述完整内容回答，不要说「看不到文件」。");
        }

        return sb.ToString().Trim();
    }

    private AgentResult Finalize(
        (string Text, string ActionKey) reply,
        PetSettings settings,
        bool usedImage,
        string provider,
        List<string>? toolTrace,
        string? userText = null)
    {
        var text = CleanReply(reply.Text);
        if (text.Length == 0)
            text = $"我在呢，{settings.PartnerName}。再说一次好不好？";

        lock (_gate)
        {
            _history.Add(new ChatTurn("assistant", text));
            TrimHistoryUnlocked();
        }

        // 每轮结束后写入本地 agent.md（摘要压缩 / 超长自动折叠）
        try
        {
            _agentMd.AppendTurnDigest(userText ?? "", text, settings.PartnerName);
            CompanionRuntime.SyncAgentMdFromMemory();
        }
        catch
        {
            // 写 agent.md 失败不阻断回复
        }

        var action = string.IsNullOrWhiteSpace(reply.ActionKey) ? InferAction(text) : reply.ActionKey;
        var affection = 2 + (usedImage ? 1 : 0) + (toolTrace is { Count: > 0 } ? 1 : 0);
        return new AgentResult(text, action, affection, provider, usedImage, toolTrace);
    }

    private static async Task<string> PostChatAsync(
        string url,
        string apiKey,
        JsonObject payload,
        CancellationToken cancellationToken,
        bool openRouter = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (openRouter)
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", AiConfig.OpenRouterReferer);
            request.Headers.TryAddWithoutValidation("X-Title", AiConfig.OpenRouterAppTitle);
        }

        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Trim(body, 240)}");
        return body;
    }

    private static bool LooksLikeDesktopRequest(string text)
    {
        string[] keys =
        [
            "看桌面", "看看桌面", "看一下桌面", "看下桌面", "截图", "看屏幕", "看一下屏幕", "看下屏幕",
            "识别桌面", "看看屏幕", "屏幕上有什么", "我在干什么", "现在在做什么", "看显示器",
            "desktop screenshot", "look at my screen", "screenshot",
        ];
        return keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static string InferAction(string text)
    {
        if (ContainsAny(text, "比心", "喜欢", "爱你", "么么", "♥", "❤"))
            return "heart";
        if (ContainsAny(text, "哈哈", "笑", "开心"))
            return "laugh";
        if (ContainsAny(text, "睡", "晚安", "休息"))
            return "sleep";
        if (ContainsAny(text, "喝水", "水杯"))
            return "drink";
        if (ContainsAny(text, "天气", "定位", "在哪", "文件", "目录", "命令", "已写入", "已移动", "已删除"))
            return "curious";
        if (ContainsAny(text, "代码", "程序", "bug", "函数", "文章", "写作"))
            return "read";
        if (ContainsAny(text, "桌面", "屏幕", "窗口", "看到", "图片"))
            return "curious";
        if (ContainsAny(text, "加油", "棒", "完成", "做好"))
            return "clap";
        if (ContainsAny(text, "害羞", "不好意思"))
            return "shy";
        return "wave";
    }

    private static bool ContainsAny(string text, params string[] keys) =>
        keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));

    private static string CleanReply(string text)
    {
        var cleaned = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (cleaned.Length > AiConfig.MaxReplyChars)
            cleaned = cleaned[..AiConfig.MaxReplyChars] + "\n…（后面还有，再说一声我继续发）";
        return cleaned;
    }

    private void TrimHistoryUnlocked()
    {
        while (_history.Count > AiConfig.MaxHistoryMessages)
            _history.RemoveAt(0);
    }

    private static string ShortModel(string model)
    {
        var s = model.Contains(':') ? model.Split(':')[0] : model;
        return s.Split('/').LastOrDefault() ?? model;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static bool LooksLikeJpeg(byte[] bytes) =>
        bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    private static bool LooksLikePng(byte[] bytes) =>
        bytes.Length > 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(AiConfig.RequestTimeoutSeconds),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XiaoShenCompanion/1.2");
        return client;
    }
}
