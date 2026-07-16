using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private readonly SemaphoreSlim _chatRequestGate = new(1, 1);
    private readonly CompanionMemoryService _memory;
    private readonly LocalAgentMdStore _agentMd;

    private sealed record ChatTurn(string Role, string Text);
    private sealed record ImageInput(byte[] Bytes, string MimeType);

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
        // 同一 Agent 只允许一个回合推进。关闭旧聊天后立刻重开时，新请求会等旧请求完成取消清理。
        await _chatRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ChatCoreAsync(
                    userText, settings, includeDesktop, hostWindow, attachments, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _chatRequestGate.Release();
        }
    }

    private async Task<AgentResult> ChatCoreAsync(
        string userText,
        PetSettings settings,
        bool includeDesktop,
        Window? hostWindow,
        IReadOnlyList<ChatAttachment>? attachments,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
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
        var images = new List<ImageInput>();
        var desktopCaptured = false;
        if (wantDesktop)
        {
            progress?.Report("正在截取桌面…");
            var capture = await Task.Run(() => DesktopCaptureService.CaptureNearWindow(hostWindow), cancellationToken)
                .ConfigureAwait(false);
            if (capture?.JpegBytes is { Length: > 0 } desktopBytes)
            {
                images.Add(new ImageInput(desktopBytes, "image/jpeg"));
                desktopCaptured = true;
            }
        }

        foreach (var attachment in attachList.Where(a =>
                     a.Kind == ChatAttachmentKind.Image && a.ImageBytes is { Length: > 0 }))
        {
            images.Add(new ImageInput(
                attachment.ImageBytes!,
                NormalizeImageMime(attachment.MimeType, attachment.ImageBytes!)));
        }

        var pendingUserTurn = new ChatTurn("user", composedUser);
        lock (_gate)
        {
            _history.Add(pendingUserTurn);
            TrimHistoryUnlocked();
        }

        try
        {
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

            // 1) 阶跃 + tools（主路径，必须快成功；失败才短兜底）
            progress?.Report("在线思考中…");
            var step = await TryAgentLoopAsync(
                providerLabel: "阶跃·3.7",
                baseUrl: AiConfig.StepBaseUrl,
                apiKey: AiConfig.StepApiKey,
                model: AiConfig.StepModel,
                openRouter: false,
                systemPrompt: systemPrompt,
                history: historySnapshot,
                images: images,
                maxTokens: AiConfig.StepDefaultMaxTokens,
                enableTools: true,
                progress: progress,
                toolTrace: toolTrace,
                extra: node => node["reasoning_effort"] = AiConfig.StepEffort,
                requestTimeoutSeconds: AiConfig.StepRequestTimeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (step is { } s)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Finalize((s.Text, InferAction(s.Text)), settings,
                    usedVisual: images.Count > 0, usedDesktop: desktopCaptured,
                    s.Provider, toolTrace, pendingUserTurn, userText: text);
            }

            // 2) 阶跃纯文本再试一次（无 tools，往往更快出 content）
            progress?.Report("阶跃换种方式再试…");
            var stepPlain = await TryAgentLoopAsync(
                providerLabel: "阶跃·3.7",
                baseUrl: AiConfig.StepBaseUrl,
                apiKey: AiConfig.StepApiKey,
                model: AiConfig.StepModel,
                openRouter: false,
                systemPrompt: systemPrompt + "\n" + AgentSystemPrompt.BuildTextOnlyAddon(),
                history: historySnapshot,
                images: images.Count > 0 ? images : Array.Empty<ImageInput>(),
                maxTokens: AiConfig.StepDefaultMaxTokens,
                enableTools: false,
                progress: progress,
                toolTrace: toolTrace,
                extra: node => node["reasoning_effort"] = AiConfig.StepEffort,
                requestTimeoutSeconds: AiConfig.StepRequestTimeoutSeconds,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (stepPlain is { } sp)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Finalize((sp.Text, InferAction(sp.Text)), settings,
                    usedVisual: images.Count > 0, usedDesktop: desktopCaptured,
                    sp.Provider, toolTrace, pendingUserTurn, userText: text);
            }

            // 3) OpenRouter 仅 1 个免费模型短超时（禁止再串行试一堆 120s）
            foreach (var model in images.Count > 0
                         ? AiConfig.OpenRouterFreeVisionModels
                         : AiConfig.OpenRouterFreeTextModels)
            {
                progress?.Report("备用线路…");
                var hit = await TryAgentLoopAsync(
                    providerLabel: $"OpenRouter·{ShortModel(model)}",
                    baseUrl: AiConfig.OpenRouterBaseUrl,
                    apiKey: AiConfig.OpenRouterApiKey,
                    model: model,
                    openRouter: true,
                    systemPrompt: systemPrompt,
                    history: historySnapshot,
                    images: images,
                    maxTokens: AiConfig.FallbackMaxTokens,
                    enableTools: false, // 免费模型 tools 不稳，且会拖慢；预取数据已够
                    progress: progress,
                    toolTrace: toolTrace,
                    extra: null,
                    requestTimeoutSeconds: AiConfig.FallbackRequestTimeoutSeconds,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (hit is { } h)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Finalize((h.Text, InferAction(h.Text)), settings,
                        usedVisual: images.Count > 0, usedDesktop: desktopCaptured,
                        h.Provider, toolTrace, pendingUserTurn, userText: text);
                }
            }

            // 4) 本地关键词 + 预取数据拼装
            var offline = ChatReplyService.Reply(text, settings, offlineMode: true, desktopRequested: wantDesktop);
            var offlineText = offline.Text;
            if (!string.IsNullOrWhiteSpace(prefetch))
                offlineText = $"{offlineText}\n\n——\n{FormatPrefetchForUser(prefetch)}";
            else if (hasTextAttach || hasImageAttach)
                offlineText = $"{offlineText}\n（在线模型暂时忙，附件我记下了。）";
            else if (wantDesktop)
                offlineText = $"{offlineText}\n（在线模型暂时不可用，先本地陪你。）";
            else
                offlineText = $"{offlineText}\n（网络不太顺，我先用本地模式陪你。）";

            cancellationToken.ThrowIfCancellationRequested();
            return Finalize((offlineText, offline.ActionKey), settings,
                usedVisual: false, usedDesktop: false,
                "本地", toolTrace, pendingUserTurn, userText: text);
        }
        catch
        {
            // 按对象身份移除本回合，绝不误删随后新窗口加入的同文本消息。
            lock (_gate)
            {
                var index = _history.FindLastIndex(turn => ReferenceEquals(turn, pendingUserTurn));
                if (index >= 0)
                    _history.RemoveAt(index);
            }
            throw;
        }
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
        IReadOnlyList<ImageInput> images,
        int maxTokens,
        bool enableTools,
        IProgress<string>? progress,
        List<string> toolTrace,
        Action<JsonObject>? extra,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            // 阶跃：max_tokens 太小会被 reasoning 吃光，content 变空 → 误判失败 → 整条备用链拖死
            if (!openRouter)
                maxTokens = Math.Max(maxTokens, AiConfig.StepMinMaxTokens);

            var messages = BuildInitialMessages(systemPrompt, history, images);
            var tools = enableTools ? WindowsAgentToolkit.BuildToolDefinitions() : null;
            var emptyContentRetries = 0;

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

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSeconds, 8, 90)));

                string json;
                try
                {
                    json = await PostChatAsync(
                        $"{baseUrl.TrimEnd('/')}/chat/completions",
                        apiKey,
                        payload,
                        timeoutCts.Token,
                        openRouter).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 单请求超时：快速失败，交给上层短兜底
                    progress?.Report($"{providerLabel} 超时…");
                    return null;
                }

                var parsed = ParseAssistantMessage(json);
                if (parsed is null)
                    return null;

                // 有 tool_calls（官方 JSON 或正文里伪 XML）：执行并回灌
                if (enableTools && parsed.ToolCalls is { Count: > 0 })
                {
                    var fromPseudo = parsed.ToolCallsFromContent;
                    if (fromPseudo)
                        messages.Add(BuildSyntheticToolCallMessage(parsed.ToolCalls));
                    else
                        messages.Add(SanitizeAssistantMessageForHistory(parsed.RawAssistantMessage));

                    var toolResults = new List<string>();
                    foreach (var call in parsed.ToolCalls)
                    {
                        progress?.Report($"执行工具：{call.Name}…");
                        toolTrace.Add(call.Name);
                        var argsObj = ParseToolArgs(call.ArgumentsJson);
                        NormalizeZodiacArgs(argsObj);

                        var result = await WindowsAgentToolkit.ExecuteAsync(call.Name, argsObj, cancellationToken)
                            .ConfigureAwait(false);
                        if (result.Length > AiConfig.MaxToolResultChars)
                            result = result[..AiConfig.MaxToolResultChars] + "\n…(工具输出截断)";
                        toolResults.Add(result);

                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["content"] = result,
                        });
                    }

                    if (fromPseudo && toolResults.Count > 0 && LooksLikeOnlyToolMarkup(parsed.Content)
                        && parsed.ToolCalls.All(c => IsDeterministicLocalTool(c.Name)))
                    {
                        return new LoopHit(string.Join("\n\n", toolResults), providerLabel);
                    }

                    if (fromPseudo)
                    {
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "上面工具结果已经给你了。请用温柔中文直接回答用户，禁止再输出 <tool_call>、<function> 等任何标签或 XML。",
                        });
                    }

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parsed.Content))
                {
                    var cleaned = StripToolMarkup(parsed.Content);
                    if (string.IsNullOrWhiteSpace(cleaned))
                        return null;
                    return new LoopHit(cleaned, providerLabel);
                }

                // content 空：finish_reason=length 时加 token 再试一次；否则立刻失败，禁止空转
                if (string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
                    && emptyContentRetries < 1)
                {
                    emptyContentRetries++;
                    maxTokens = Math.Min(Math.Max(maxTokens * 2, 1600), 3200);
                    progress?.Report("回复被截断，再补一刀…");
                    continue;
                }

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

    /// <summary>回灌历史时去掉 reasoning 字段，避免下一轮上下文膨胀拖慢。</summary>
    private static JsonObject SanitizeAssistantMessageForHistory(JsonObject raw)
    {
        var clone = raw.DeepClone() as JsonObject ?? new JsonObject { ["role"] = "assistant" };
        clone.Remove("reasoning");
        clone.Remove("reasoning_content");
        return clone;
    }

    private sealed record ToolCall(string Id, string Name, string ArgumentsJson);

    private sealed record ParsedAssistant(
        string? Content,
        IReadOnlyList<ToolCall>? ToolCalls,
        JsonObject RawAssistantMessage,
        bool ToolCallsFromContent = false,
        string? FinishReason = null);

    private static ParsedAssistant? ParseAssistantMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                return null;

            var choice = choices[0];
            var message = choice.GetProperty("message");
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

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
            var fromContent = false;
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

            // 模型把工具调用写进正文（<tool_call>…）时也要执行，否则用户会看到「乱码」
            if ((calls is null || calls.Count == 0) && !string.IsNullOrWhiteSpace(content))
            {
                var embedded = ParseEmbeddedToolCalls(content);
                if (embedded.Count > 0)
                {
                    calls = embedded;
                    fromContent = true;
                }
            }

            if (calls is { Count: 0 })
                calls = null;

            return new ParsedAssistant(content?.Trim(), calls, raw, fromContent, finishReason);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析模型误输出的伪工具调用，例如：
    /// &lt;tool_call&gt;&lt;function=zodiac_analyze&gt;&lt;parameter=sign&gt;双鱼座&lt;/parameter&gt;...
    /// </summary>
    private static List<ToolCall> ParseEmbeddedToolCalls(string content)
    {
        var list = new List<ToolCall>();
        if (string.IsNullOrWhiteSpace(content))
            return list;

        // 形式 A：<tool_call> <function=name> <parameter=k>v</parameter> </function> </tool_call>
        var blockRx = new Regex(
            @"<tool_call>\s*<function\s*=\s*([a-zA-Z0-9_]+)>(.*?)</function>\s*</tool_call>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match m in blockRx.Matches(content))
        {
            var name = m.Groups[1].Value.Trim();
            var body = m.Groups[2].Value;
            var args = new JsonObject();
            foreach (Match pm in Regex.Matches(body,
                         @"<parameter\s*=\s*([a-zA-Z0-9_]+)>\s*(.*?)\s*</parameter>",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                args[pm.Groups[1].Value.Trim()] = pm.Groups[2].Value.Trim();
            }

            list.Add(new ToolCall("pseudo_" + Guid.NewGuid().ToString("N")[..12], name, args.ToJsonString()));
        }

        if (list.Count > 0)
            return list;

        // 形式 B：invoke tool xxx 或 function call zodiac_analyze(...)
        var invokeRx = new Regex(
            @"(?:invoke\s+tool|function\s*call|call\s+tool)\s+([a-zA-Z0-9_]+)\s*[\({]([^)}\n]*)[\)}]",
            RegexOptions.IgnoreCase);
        foreach (Match m in invokeRx.Matches(content))
        {
            var name = m.Groups[1].Value.Trim();
            var rawArgs = m.Groups[2].Value.Trim();
            var args = new JsonObject { ["query"] = rawArgs };
            list.Add(new ToolCall("pseudo_" + Guid.NewGuid().ToString("N")[..12], name, args.ToJsonString()));
        }

        return list;
    }

    private static JsonObject BuildSyntheticToolCallMessage(IReadOnlyList<ToolCall> calls)
    {
        var arr = new JsonArray();
        foreach (var c in calls)
        {
            arr.Add(new JsonObject
            {
                ["id"] = c.Id,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = c.Name,
                    ["arguments"] = c.ArgumentsJson,
                },
            });
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = arr,
        };
    }

    private static JsonObject ParseToolArgs(string? argumentsJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return new JsonObject();
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject { ["query"] = argumentsJson };
        }
    }

    /// <summary>把 sign/date 等别名参数归一到 zodiac_analyze 的 query。</summary>
    private static void NormalizeZodiacArgs(JsonObject args)
    {
        if (args.ContainsKey("query") && !string.IsNullOrWhiteSpace(args["query"]?.ToString()))
            return;
        var sign = args["sign"]?.ToString()?.Trim().Trim('"');
        var date = args["date"]?.ToString()?.Trim().Trim('"');
        if (!string.IsNullOrWhiteSpace(date))
            args["query"] = date;
        else if (!string.IsNullOrWhiteSpace(sign))
            args["query"] = sign;
    }

    private static bool IsDeterministicLocalTool(string name) =>
        name is "zodiac_analyze" or "daily_card" or "get_system_info" or "get_system_monitor"
            or "memory_list" or "memo_list" or "agent_md_path" or "get_special_folder";

    private static bool LooksLikeOnlyToolMarkup(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return true;
        var stripped = StripToolMarkup(content);
        return string.IsNullOrWhiteSpace(stripped) || stripped.Length < 8;
    }

    private static string StripToolMarkup(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        var cleaned = text;
        cleaned = Regex.Replace(cleaned,
            @"<tool_call>[\s\S]*?</tool_call>",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"</?tool_call>|</?function[^>]*>|</?parameter[^>]*>",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned,
            @"<function\s*=\s*[^>]+>[\s\S]*?</function>",
            "",
            RegexOptions.IgnoreCase);
        // 残留半截标签
        cleaned = Regex.Replace(cleaned, @"</?(?:tool_call|function|parameter)[^>]*>?", "", RegexOptions.IgnoreCase);
        return cleaned.Trim();
    }

    private static JsonArray BuildInitialMessages(
        string systemPrompt,
        IReadOnlyList<ChatTurn> history,
        IReadOnlyList<ImageInput> images)
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
        if (images.Count > 0 && last.Role == "user")
        {
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = last.Text },
            };
            foreach (var image in images)
            {
                var dataUrl = $"data:{image.MimeType};base64,{Convert.ToBase64String(image.Bytes)}";
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = dataUrl },
                });
            }
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
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
        bool usedVisual,
        bool usedDesktop,
        string provider,
        List<string>? toolTrace,
        ChatTurn pendingUserTurn,
        string? userText = null)
    {
        var text = CleanReply(reply.Text);
        if (text.Length == 0)
            text = $"我在呢，{settings.PartnerName}。再说一次好不好？";

        var historyStillContainsTurn = false;
        lock (_gate)
        {
            historyStillContainsTurn = _history.Any(turn => ReferenceEquals(turn, pendingUserTurn));
            if (historyStillContainsTurn)
            {
                _history.Add(new ChatTurn("assistant", text));
                TrimHistoryUnlocked();
            }
        }

        // 每轮结束后写入本地 agent.md（摘要压缩 / 超长自动折叠）
        try
        {
            if (historyStillContainsTurn)
            {
                _agentMd.AppendTurnDigest(userText ?? "", text, settings.PartnerName);
                CompanionRuntime.SyncAgentMdFromMemory();
            }
        }
        catch
        {
            // 写 agent.md 失败不阻断回复
        }

        var action = string.IsNullOrWhiteSpace(reply.ActionKey) ? InferAction(text) : reply.ActionKey;
        var affection = 2 + (usedVisual ? 1 : 0) + (toolTrace is { Count: > 0 } ? 1 : 0);
        return new AgentResult(text, action, affection, provider, usedDesktop, toolTrace);
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
        var cleaned = StripToolMarkup((text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
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

    private static string NormalizeImageMime(string? mimeType, byte[] bytes)
    {
        var mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        if (mime is "image/jpeg" or "image/png" or "image/gif" or "image/webp" or "image/bmp")
            return mime;
        if (LooksLikeJpeg(bytes))
            return "image/jpeg";
        if (LooksLikePng(bytes))
            return "image/png";
        return "image/jpeg";
    }

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
