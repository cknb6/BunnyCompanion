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
    byte[]? ImageBytes,
    /// <summary>本机绝对路径（拖拽/选择文件时提供，便于 Agent 用工具读写）。</summary>
    string? FullPath = null);

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

    /// <summary>进程级定位缓存：首次对话静默取一次，之后每轮复用，避免每轮联网。</summary>
    private static string? _cachedLocationBrief;
    private static DateTime _cachedLocationAtUtc = DateTime.MinValue;

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

        // 先剔除过大图片字节：避免正文写「图已附在消息中」而实际 vision 列表为空
        for (var i = 0; i < attachList.Count; i++)
        {
            var a = attachList[i];
            if (a.Kind != ChatAttachmentKind.Image || a.ImageBytes is not { Length: > 0 } imgBytes)
                continue;
            if (imgBytes.Length <= AiConfig.MaxImageBytesSoft)
                continue;
            attachList[i] = a with { ImageBytes = null };
        }

        var hasImageAttach = attachList.Any(a => a.Kind == ChatAttachmentKind.Image && a.ImageBytes is { Length: > 0 });
        var hasTextAttach = attachList.Any(a => a.Kind == ChatAttachmentKind.Text && !string.IsNullOrWhiteSpace(a.TextContent));
        var hasPathAttach = attachList.Any(a =>
            a.Kind == ChatAttachmentKind.Other && !string.IsNullOrWhiteSpace(a.FullPath));
        // 仅有过大图被剔除后：仍可按路径处理
        var hasPathOnlyImage = attachList.Any(a =>
            a.Kind == ChatAttachmentKind.Image
            && (a.ImageBytes is null || a.ImageBytes.Length == 0)
            && !string.IsNullOrWhiteSpace(a.FullPath));

        if (text.Length == 0)
        {
            if (includeDesktop)
                text = "帮我看一下桌面，说说屏幕上有什么，给点贴心建议就好。";
            else if (hasImageAttach)
                text = "看看我发的图，跟我说你看到了什么。";
            else if (hasTextAttach)
                text = "看看我发的文件，帮我读一下并说说重点。";
            else if (hasPathAttach || hasPathOnlyImage)
                text = "我拖了本机文件进来，请按路径用工具查看或处理，并告诉我结果。";
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
            if (images.Count >= AiConfig.MaxImageAttachments)
                break;
            var bytes = attachment.ImageBytes!;
            if (bytes.Length > AiConfig.MaxImageBytesSoft)
                continue;

            images.Add(new ImageInput(bytes, NormalizeImageMime(attachment.MimeType, bytes)));
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

            var officeMode = settings.IsOfficeMode;
            var systemPrompt = officeMode
                ? AgentSystemPrompt.BuildOffice(settings)
                : AgentSystemPrompt.Build(settings);
            var memoryBlock = _memory.FormatForSystemPrompt();
            if (!string.IsNullOrWhiteSpace(memoryBlock))
                systemPrompt += "\n\n" + memoryBlock;

            // 当前情境：位置（进程级缓存）+ 前台活动（实时），让 Agent 每轮都知道"用户在哪、在干嘛"
            try
            {
                var contextBlock = await BuildCurrentContextAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(contextBlock))
                    systemPrompt += "\n\n" + contextBlock;
            }
            catch { /* 情境注入失败不阻断对话 */ }

            // 本地 agent.md：滚动摘要 + 近期压缩对话（长期记忆主载体之一）
            try
            {
                var agentMdBlock = _agentMd.FormatForSystemPrompt(maxChars: officeMode ? 4000 : 5500);
                if (!string.IsNullOrWhiteSpace(agentMdBlock) && agentMdBlock.Length > 80)
                    systemPrompt += "\n\n" + agentMdBlock;
            }
            catch { /* ignore */ }

            // 办公：注入进行中计划 + 技能触发词自动匹配
            if (officeMode)
            {
                try
                {
                    var planBlock = CompanionRuntime.OfficePlan.FormatForSystemPrompt();
                    if (!string.IsNullOrWhiteSpace(planBlock))
                        systemPrompt += "\n\n" + planBlock;
                }
                catch { /* ignore */ }

                try
                {
                    var skill = CompanionRuntime.Skills.Match(text);
                    if (skill is not null)
                    {
                        var body = CompanionRuntime.Skills.GetBody(skill.Name);
                        systemPrompt += "\n\n# 已匹配本地技能（优先 skill_get / skill_run）\n";
                        systemPrompt += $"技能名: {skill.Name}\n";
                        if (!string.IsNullOrWhiteSpace(skill.Description))
                            systemPrompt += $"说明: {skill.Description}\n";
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            var clip = body.Length > 2500 ? body[..2500] + "\n…" : body;
                            systemPrompt += "技能正文:\n" + clip;
                        }
                    }
                }
                catch { /* ignore */ }
            }

            var toolTrace = new List<string>();
            if (officeMode)
                toolTrace.Add("mode:office");

            // 本地意图预取：定位/天气等（办公模式下对纯天气仍可用；复杂任务不预取截断 tools）
            var prefetch = officeMode && LooksLikeHeavyOfficeTask(text)
                ? null
                : await MaybePrefetchLocalFactsAsync(text, progress, cancellationToken).ConfigureAwait(false);
            var hasPrefetch = !string.IsNullOrWhiteSpace(prefetch);
            if (hasPrefetch)
            {
                systemPrompt += "\n\n# 本回合已预取的真实数据（必须直接采用，禁止编造数字/城市）\n" + prefetch;
                systemPrompt += "\n\n【重要】上面已有真实工具结果。请用温柔中文直接回答用户，不要再调用工具，不要输出 XML/标签。";
                toolTrace.Add("prefetch");
            }

            // 1a) 已有预取：阶跃纯文本总结（无 tools）—— 天气/定位芯片的主路径
            if (hasPrefetch && !officeMode)
            {
                progress?.Report("在线整理中…");
                var stepPrefetch = await TryAgentLoopAsync(
                    providerLabel: "阶跃·3.7",
                    baseUrl: AiConfig.StepBaseUrl,
                    apiKey: AiConfig.StepApiKey,
                    model: AiConfig.StepModel,
                    openRouter: false,
                    systemPrompt: systemPrompt,
                    history: historySnapshot,
                    images: Array.Empty<ImageInput>(),
                    maxTokens: AiConfig.StepDefaultMaxTokens,
                    enableTools: false,
                    officeMode: false,
                    progress: progress,
                    toolTrace: toolTrace,
                    accumulatedToolResults: null,
                    extra: node => node["reasoning_effort"] = AiConfig.StepEffort,
                    requestTimeoutSeconds: Math.Min(AiConfig.StepRequestTimeoutSeconds, 28),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (stepPrefetch is { } sp0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Finalize((sp0.Text, InferAction(sp0.Text)), settings,
                        usedVisual: false, usedDesktop: false,
                        sp0.Provider, toolTrace, pendingUserTurn, userText: text);
                }

                // 阶跃总结失败：直接用预取数据本地口语化，绝不为此再去备用线路拖很久
                progress?.Report("用本机结果回答…");
                var localFromPrefetch = FormatPrefetchAsCompanionReply(prefetch!, settings.PartnerName);
                cancellationToken.ThrowIfCancellationRequested();
                return Finalize((localFromPrefetch, InferAction(localFromPrefetch)), settings,
                    usedVisual: false, usedDesktop: false,
                    "阶跃·预取", toolTrace, pendingUserTurn, userText: text);
            }

            // 1b) 阶跃 + tools（陪伴 / 办公 Agent）
            progress?.Report(officeMode ? "办公 Agent 规划执行中…" : "在线思考中…");
            var stepMaxTokens = officeMode ? AiConfig.StepOfficeMaxTokens : AiConfig.StepDefaultMaxTokens;
            var stepTimeout = officeMode ? AiConfig.StepOfficeTimeoutSeconds : AiConfig.StepRequestTimeoutSeconds;
            var stepEffort = officeMode ? AiConfig.StepOfficeEffort : AiConfig.StepEffort;
            // 办公：累计全部工具原文，循环内失败也能交付，避免「工具跑了却整链 null」
            var officeToolBag = officeMode ? new List<string>() : null;
            var step = await TryAgentLoopAsync(
                providerLabel: officeMode ? "阶跃·办公" : "阶跃·3.7",
                baseUrl: AiConfig.StepBaseUrl,
                apiKey: AiConfig.StepApiKey,
                model: AiConfig.StepModel,
                openRouter: false,
                systemPrompt: systemPrompt,
                history: historySnapshot,
                images: images,
                maxTokens: stepMaxTokens,
                enableTools: true,
                officeMode: officeMode,
                progress: progress,
                toolTrace: toolTrace,
                accumulatedToolResults: officeToolBag,
                extra: node => node["reasoning_effort"] = stepEffort,
                requestTimeoutSeconds: stepTimeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (step is { } s)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Finalize((s.Text, InferAction(s.Text)), settings,
                    usedVisual: images.Count > 0, usedDesktop: desktopCaptured,
                    s.Provider, toolTrace, pendingUserTurn, userText: text);
            }

            // 办公：主循环 null 时先用已累计工具结果交付；再试一轮「无 tools 总结」
            if (officeMode)
            {
                if (officeToolBag is { Count: > 0 })
                {
                    progress?.Report("工具已执行，整理结果…");
                    // 再试一次：把工具结果塞进 system，强制纯文本总结（不再调工具，避免半截停）
                    var salvagePrompt = systemPrompt +
                        "\n\n# 本回合已执行工具的真实结果（必须据此用中文交付，禁止再说没跑通）\n" +
                        string.Join("\n\n", officeToolBag.Take(8)) +
                        "\n\n请直接给用户完整中文结论，不要空回复，不要再调用工具。";
                    var salvage = await TryAgentLoopAsync(
                        providerLabel: "阶跃·办公·收尾",
                        baseUrl: AiConfig.StepBaseUrl,
                        apiKey: AiConfig.StepApiKey,
                        model: AiConfig.StepModel,
                        openRouter: false,
                        systemPrompt: salvagePrompt,
                        history: historySnapshot,
                        images: Array.Empty<ImageInput>(),
                        maxTokens: Math.Max(AiConfig.StepOfficeMaxTokens, 2000),
                        enableTools: false,
                        officeMode: true,
                        progress: progress,
                        toolTrace: toolTrace,
                        accumulatedToolResults: null,
                        extra: node => node["reasoning_effort"] = "low",
                        requestTimeoutSeconds: 55,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (salvage is { } sv && !string.IsNullOrWhiteSpace(sv.Text))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Finalize((sv.Text, InferAction(sv.Text)), settings,
                            usedVisual: false, usedDesktop: desktopCaptured,
                            sv.Provider, toolTrace, pendingUserTurn, userText: text);
                    }

                    // 模型仍空：本地拼装工具结果，绝不报「没跑通」
                    var localDeliver = FormatToolResultsFallback(officeToolBag);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Finalize((localDeliver, "curious"), settings,
                        usedVisual: false, usedDesktop: desktopCaptured,
                        "办公·工具兜底", toolTrace, pendingUserTurn, userText: text);
                }

                var officeFail =
                    "办公 Agent 这轮在线接口不稳定（超时或空回复），还没来得及执行本机工具。\n" +
                    "请再发一次，或把任务拆小一点（例如先「列桌面文件」再「移动 pdf」）。\n" +
                    (toolTrace.Count > 1
                        ? "痕迹: " + string.Join(" → ", toolTrace.Distinct().Take(12))
                        : "");
                cancellationToken.ThrowIfCancellationRequested();
                return Finalize((officeFail.Trim(), "sad"), settings,
                    usedVisual: false, usedDesktop: desktopCaptured,
                    "办公·中断", toolTrace, pendingUserTurn, userText: text);
            }

            // 2) 阶跃纯文本再试（无 tools）；进度文案不暴露供应商名
            progress?.Report("换种方式再想想…");
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
                officeMode: false,
                progress: progress,
                toolTrace: toolTrace,
                accumulatedToolResults: null,
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

            // 3) 备用线路：仅纯闲聊/看图等无预取时，短超时 1 次
            foreach (var model in images.Count > 0
                         ? AiConfig.OpenRouterFreeVisionModels
                         : AiConfig.OpenRouterFreeTextModels)
            {
                progress?.Report("备用线路…");
                var hit = await TryAgentLoopAsync(
                    providerLabel: $"备用·{ShortModel(model)}",
                    baseUrl: AiConfig.OpenRouterBaseUrl,
                    apiKey: AiConfig.OpenRouterApiKey,
                    model: model,
                    openRouter: true,
                    systemPrompt: systemPrompt,
                    history: historySnapshot,
                    images: images,
                    maxTokens: AiConfig.FallbackMaxTokens,
                    enableTools: false,
                    officeMode: false,
                    progress: progress,
                    toolTrace: toolTrace,
                    accumulatedToolResults: null,
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

            // 4) 本地关键词
            var offline = ChatReplyService.Reply(text, settings, offlineMode: true, desktopRequested: wantDesktop);
            var offlineText = offline.Text;
            if (hasTextAttach || hasImageAttach || hasPathAttach)
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
        bool officeMode,
        IProgress<string>? progress,
        List<string> toolTrace,
        List<string>? accumulatedToolResults,
        Action<JsonObject>? extra,
        int requestTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        List<string>? lastToolResults = null;
        try
        {
            // 阶跃：max_tokens 太小会被 reasoning 吃光，content 变空 → 误判失败 → 整条备用链拖死
            if (!openRouter)
            {
                var minTok = officeMode ? AiConfig.StepOfficeMinMaxTokens : AiConfig.StepMinMaxTokens;
                maxTokens = Math.Max(maxTokens, minTok);
            }

            var messages = BuildInitialMessages(systemPrompt, history, images);
            var tools = enableTools ? WindowsAgentToolkit.BuildToolDefinitions() : null;
            var emptyContentRetries = 0;
            var emptyAfterToolsRetries = 0;
            var maxEmptyAfterTools = officeMode ? AiConfig.OfficeEmptyAfterToolsRetries : 1;
            var maxRounds = officeMode ? AiConfig.MaxToolRoundsOffice : AiConfig.MaxToolRounds;

            LoopHit? DeliverToolsOrNull()
            {
                if (accumulatedToolResults is { Count: > 0 })
                    return new LoopHit(FormatToolResultsFallback(accumulatedToolResults), providerLabel);
                if (lastToolResults is { Count: > 0 })
                    return new LoopHit(FormatToolResultsFallback(lastToolResults), providerLabel);
                return null;
            }

            for (var round = 0; round < maxRounds; round++)
            {
                var payload = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = messages,
                    ["temperature"] = officeMode ? 0.35 : 0.7,
                    ["max_tokens"] = maxTokens,
                };
                if (tools is not null)
                {
                    payload["tools"] = tools.DeepClone();
                    payload["tool_choice"] = "auto";
                    if (!openRouter)
                        payload["parallel_tool_calls"] = true;
                }

                extra?.Invoke(payload);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var cap = officeMode ? 150 : 90;
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(requestTimeoutSeconds, 8, cap)));

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
                    progress?.Report("想得有点久，先用已有结果…");
                    return DeliverToolsOrNull();
                }

                var parsed = ParseAssistantMessage(json);
                if (parsed is null)
                {
                    // 解析失败也不得丢掉已执行工具
                    var fallback = DeliverToolsOrNull();
                    if (fallback is not null)
                        return fallback;
                    return null;
                }

                // 有 tool_calls：执行并回灌（content 空是正常的）
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
                        NormalizePathArgs(call.Name, argsObj);

                        string result;
                        try
                        {
                            result = await WindowsAgentToolkit.ExecuteAsync(call.Name, argsObj, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            result = $"工具执行异常: {ex.GetType().Name}: {ex.Message}";
                        }

                        if (result.Length > AiConfig.MaxToolResultChars)
                            result = result[..AiConfig.MaxToolResultChars] + "\n…(工具输出截断)";
                        var block = $"【{call.Name}】\n{result}";
                        toolResults.Add(block);
                        accumulatedToolResults?.Add(block);

                        messages.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = call.Id,
                            ["name"] = call.Name,
                            ["content"] = result,
                        });
                    }

                    lastToolResults = toolResults;
                    // 每跑完工具就清零「空 content 重试」，给总结多几次机会
                    emptyAfterToolsRetries = 0;

                    if (fromPseudo && toolResults.Count > 0 && LooksLikeOnlyToolMarkup(parsed.Content)
                        && parsed.ToolCalls.All(c => IsDeterministicLocalTool(c.Name)))
                    {
                        return new LoopHit(string.Join("\n\n", toolResults), providerLabel);
                    }

                    // 办公：优先能交付就交付，避免一直 tool 到超时空 content
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = officeMode
                            ? "工具结果已回灌。请立刻二选一（禁止空回复）：\n" +
                              "A) 信息已够：用简体中文直接交付结论（做了什么、关键结果、下一步建议）；\n" +
                              "B) 还缺一步：只再调用必要工具，不要重复已成功的调用。\n" +
                              "batch_* 预览后须把清单给用户并停下，禁止同轮 dry_run=false。\n" +
                              "禁止输出 tool_call/XML。必须有可见中文正文。"
                            : "工具结果已全部给你。请用温柔简体中文直接回答用户：" +
                              "总结要点、说明已做了什么；禁止输出 <tool_call>、<function>、XML 或 JSON 伪代码；" +
                              "不要重复工具原始日志，改成口语。",
                    });

                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parsed.Content))
                {
                    var cleaned = StripToolMarkup(parsed.Content);
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        var fb = DeliverToolsOrNull();
                        if (fb is not null)
                            return fb;
                        return null;
                    }

                    return new LoopHit(cleaned, providerLabel);
                }

                // content 空 + 已有工具：多催几轮（办公 3 次），仍空则本地拼装
                if (lastToolResults is { Count: > 0 } || accumulatedToolResults is { Count: > 0 })
                {
                    if (emptyAfterToolsRetries < maxEmptyAfterTools)
                    {
                        emptyAfterToolsRetries++;
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] =
                                "你刚才返回了空正文。请现在只用中文写最终回答（至少 2 句），" +
                                "根据已有工具结果总结，禁止空回复，禁止再调工具，禁止 XML。",
                        });
                        progress?.Report($"工具已跑完，正在整理回答（{emptyAfterToolsRetries}/{maxEmptyAfterTools}）…");
                        // 加大 token，减轻 reasoning 占满
                        if (officeMode)
                            maxTokens = Math.Min(Math.Max(maxTokens, 2400), 4000);
                        continue;
                    }

                    return DeliverToolsOrNull();
                }

                if (string.Equals(parsed.FinishReason, "length", StringComparison.OrdinalIgnoreCase)
                    && emptyContentRetries < 1)
                {
                    emptyContentRetries++;
                    maxTokens = Math.Min(Math.Max(maxTokens * 2, 1600), officeMode ? 4000 : 3200);
                    progress?.Report("回复被截断，再补一刀…");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parsed.ReasoningHint))
                {
                    var hint = TryExtractReadableReplyFromReasoning(parsed.ReasoningHint);
                    if (!string.IsNullOrWhiteSpace(hint))
                        return new LoopHit(hint!, providerLabel);
                }

                // 空轮：办公再给一次「必须输出」机会
                if (officeMode && emptyContentRetries < 2)
                {
                    emptyContentRetries++;
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = "请直接用中文回答用户问题，不要空回复。若需要工具请调用 tools。",
                    });
                    continue;
                }

                return DeliverToolsOrNull();
            }

            var afterRounds = DeliverToolsOrNull();
            if (afterRounds is not null)
                return afterRounds;

            return new LoopHit(
                officeMode
                    ? $"本回合工具步骤已较多（上限 {maxRounds}）。请根据上面结果继续说下一步，或缩小任务范围。"
                    : "工具步骤有点多，我先停一下～你再说具体一点目标，我继续帮你弄。",
                providerLabel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is TaskCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            progress?.Report("线路有点不顺…");
            System.Diagnostics.Debug.WriteLine($"Agent loop fail [{providerLabel}]: {ex.Message}");
            if (accumulatedToolResults is { Count: > 0 })
                return new LoopHit(FormatToolResultsFallback(accumulatedToolResults), providerLabel);
            if (lastToolResults is { Count: > 0 })
                return new LoopHit(FormatToolResultsFallback(lastToolResults), providerLabel);
            return null;
        }
    }

    /// <summary>回灌历史时去掉 reasoning，且 tool_calls 时 content 空串改 null（Step 更稳）。</summary>
    private static JsonObject SanitizeAssistantMessageForHistory(JsonObject raw)
    {
        var clone = raw.DeepClone() as JsonObject ?? new JsonObject { ["role"] = "assistant" };
        clone.Remove("reasoning");
        clone.Remove("reasoning_content");
        if (clone["tool_calls"] is not null)
        {
            var c = clone["content"]?.ToString();
            if (string.IsNullOrWhiteSpace(c) || c is "null" or "\"\"")
                clone["content"] = null;
        }

        return clone;
    }

    private static string FormatToolResultsFallback(IReadOnlyList<string> toolResults)
    {
        var sb = new StringBuilder();
        sb.AppendLine("我已经帮你查/做完啦，结果如下：");
        sb.AppendLine();
        foreach (var r in toolResults.Take(6))
            sb.AppendLine(r.Length > 1200 ? r[..1200] + "…" : r);
        sb.AppendLine();
        sb.Append("还有要继续弄的直接说～");
        return sb.ToString().Trim();
    }

    /// <summary>弱兜底：从 reasoning 文本里抓引号内或末尾短句，避免完全空回复。</summary>
    private static string? TryExtractReadableReplyFromReasoning(string reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
            return null;
        // 优先取中文引号内容
        var m = Regex.Match(reasoning, "[「\"“]([^」\"”]{4,80})[」\"”]");
        if (m.Success)
            return m.Groups[1].Value.Trim();
        // 末尾像完整句的中文
        m = Regex.Match(reasoning, @"([\u4e00-\u9fff][^。！？\n]{6,60}[。！？])\s*$");
        if (m.Success)
            return m.Groups[1].Value.Trim();
        return null;
    }

    /// <summary>规范化路径类工具参数（空 path、~/Desktop 等）。</summary>
    private static void NormalizePathArgs(string toolName, JsonObject args)
    {
        static void Fix(JsonObject a, string key)
        {
            if (!a.ContainsKey(key))
                return;
            var v = a[key]?.ToString()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(v))
            {
                if (key is "path" or "root")
                    a[key] = "桌面";
                return;
            }

            // 模型常写 ~/Desktop、~/Documents
            if (v.StartsWith("~/Desktop", StringComparison.OrdinalIgnoreCase)
                || v.Equals("~\\Desktop", StringComparison.OrdinalIgnoreCase)
                || v.Equals("~/桌面", StringComparison.OrdinalIgnoreCase))
                a[key] = "桌面";
            else if (v.StartsWith("~/Documents", StringComparison.OrdinalIgnoreCase)
                     || v.StartsWith("~/文档", StringComparison.OrdinalIgnoreCase))
                a[key] = "文档";
            else if (v.StartsWith("~/Downloads", StringComparison.OrdinalIgnoreCase)
                     || v.StartsWith("~/下载", StringComparison.OrdinalIgnoreCase))
                a[key] = "下载";
            else if (v is "~" or "~/" or "~\\")
                a[key] = "用户目录";
        }

        switch (toolName)
        {
            case "list_dir":
            case "read_file":
            case "write_file":
            case "append_file":
            case "delete_path":
            case "open_path":
            case "create_directory":
                Fix(args, "path");
                break;
            case "search_files":
                Fix(args, "root");
                break;
            case "move_path":
            case "copy_path":
                Fix(args, "source");
                Fix(args, "destination");
                break;
        }
    }

    private sealed record ToolCall(string Id, string Name, string ArgumentsJson);

    private sealed record ParsedAssistant(
        string? Content,
        IReadOnlyList<ToolCall>? ToolCalls,
        JsonObject RawAssistantMessage,
        bool ToolCallsFromContent = false,
        string? FinishReason = null,
        string? ReasoningHint = null);

    private static ParsedAssistant? ParseAssistantMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            // 明确错误体（Step 有时 200 外也会包 error）
            if (root.TryGetProperty("error", out var errNode))
            {
                var msg = errNode.ValueKind == JsonValueKind.Object && errNode.TryGetProperty("message", out var em)
                    ? em.GetString()
                    : errNode.ToString();
                System.Diagnostics.Debug.WriteLine("API error body: " + msg);
                return null;
            }

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
            if (message.TryGetProperty("content", out var contentElement)
                && contentElement.ValueKind != JsonValueKind.Null)
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
                        // 部分多模态返回 type=text
                        else if (part.TryGetProperty("type", out var ty)
                                 && ty.GetString() == "text"
                                 && part.TryGetProperty("text", out var t2))
                            sb.Append(t2.GetString());
                    }

                    content = sb.ToString();
                }
            }

            string? reasoningHint = null;
            if (message.TryGetProperty("reasoning", out var rs) && rs.ValueKind == JsonValueKind.String)
                reasoningHint = rs.GetString();
            else if (message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                reasoningHint = rc.GetString();

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

            // 空串 content 统一当 null，避免后续 IsNullOrWhiteSpace 误判路径不一致
            content = string.IsNullOrWhiteSpace(content) ? null : content.Trim();

            return new ParsedAssistant(content, calls, raw, fromContent, finishReason, reasoningHint);
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
            or "memory_list" or "memo_list" or "agent_md_path" or "get_special_folder"
            or "plan_status" or "plan_set" or "plan_tick";

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
    /// 组装"当前情境"块：用户大概位置（进程级缓存，6 小时刷新）+ 当前前台活动（实时）。
    /// 每轮注入系统提示，让 Agent 跨会话稳定知道"用户是谁、在哪、在干嘛"。
    /// 位置只在首次或过期时联网，避免每轮调用公网 IP 库。
    /// </summary>
    private static async Task<string?> BuildCurrentContextAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 当前情境（每轮自动注入，请自然结合，勿生硬复读）");

        // 位置：进程级缓存，6 小时刷新
        var nowUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(_cachedLocationBrief)
            || nowUtc - _cachedLocationAtUtc > TimeSpan.FromHours(6))
        {
            try
            {
                var loc = await WindowsAgentToolkit.ExecuteAsync("get_location", new JsonObject(), ct)
                    .ConfigureAwait(false);
                _cachedLocationBrief = ExtractLocationBrief(loc);
                _cachedLocationAtUtc = nowUtc;
                DebugLogService.Log("context", $"定位刷新 brief={_cachedLocationBrief}");
            }
            catch (Exception ex)
            {
                DebugLogService.LogError("context", "定位刷新失败（用旧缓存或省略）", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(_cachedLocationBrief))
            sb.AppendLine($"- 用户大概位置：{_cachedLocationBrief}");

        // 当前活动：前台窗口（毫秒级本地 API，每轮取）
        try
        {
            var activity = SystemMonitorService.GetCurrentActivity();
            if (!string.IsNullOrWhiteSpace(activity))
                sb.AppendLine($"- 用户当前活动：{activity}");
        }
        catch (Exception ex)
        {
            DebugLogService.LogError("context", "取当前活动失败", ex);
        }

        var text = sb.ToString().Trim();
        return text.Length > 80 ? text : null;
    }

    /// <summary>从 get_location 完整输出里提取一句话位置摘要（国家+省+市）。</summary>
    private static string ExtractLocationBrief(string locFull)
    {
        if (string.IsNullOrWhiteSpace(locFull))
            return "";
        string? country = null, province = null, city = null;
        foreach (var line in locFull.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("国家:", StringComparison.Ordinal)) country = t["国家:".Length..].Trim();
            else if (t.StartsWith("省/州:", StringComparison.Ordinal)) province = t["省/州:".Length..].Trim();
            else if (t.StartsWith("城市:", StringComparison.Ordinal)) city = t["城市:".Length..].Trim();
        }
        var parts = new[] { country, province, city }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count > 0 ? string.Join(" · ", parts) : "";
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

    /// <summary>
    /// 预取成功但模型空回复时：把工具原文整理成陪伴口吻，避免再跳备用线路。
    /// </summary>
    private static string FormatPrefetchAsCompanionReply(string prefetch, string partnerName)
    {
        var body = FormatPrefetchForUser(prefetch);
        // 去掉过长的技术噪音
        if (body.Length > 1800)
            body = body[..1800] + "…";
        body = Regex.Replace(body, @"\*\*(.+?)\*\*", "$1");
        body = Regex.Replace(body, @"#{1,6}\s*", "");
        return
            $"{partnerName}，我帮你查好啦～\n\n{body.Trim()}\n\n" +
            "还有想问的直接说，小申在桌角陪你～";
    }

    private static string ComposeUserMessage(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
            return text;

        var sb = new StringBuilder();
        sb.AppendLine(string.IsNullOrWhiteSpace(text) ? "请查看我拖进/发来的附件，按需处理。" : text);
        sb.AppendLine();

        // 仅声明确实会进入 vision 请求的图片，避免模型误以为「已看图」
        var imageNames = attachments
            .Where(a => a.Kind == ChatAttachmentKind.Image && a.ImageBytes is { Length: > 0 })
            .Select(a => a.FileName)
            .ToList();
        if (imageNames.Count > 0)
            sb.AppendLine($"【用户上传了图片】{string.Join("、", imageNames)}（图已附在消息中，请仔细看）");

        foreach (var a in attachments.Where(x =>
                     x.Kind == ChatAttachmentKind.Image
                     && (x.ImageBytes is null || x.ImageBytes.Length == 0)
                     && !string.IsNullOrWhiteSpace(x.FullPath)))
        {
            sb.AppendLine();
            sb.AppendLine($"【图片过大未能嵌入预览：{a.FileName}】");
            sb.AppendLine($"本机路径：{a.FullPath}");
            sb.AppendLine("请用工具按路径处理，或请用户换较小图片。");
        }

        foreach (var a in attachments.Where(x => x.Kind == ChatAttachmentKind.Text))
        {
            var body = a.TextContent ?? string.Empty;
            if (body.Length > AiConfig.MaxAttachmentTextChars)
                body = body[..AiConfig.MaxAttachmentTextChars] + "\n…（后文过长已截断）";
            sb.AppendLine();
            sb.AppendLine($"【附件文件：{a.FileName}】");
            if (!string.IsNullOrWhiteSpace(a.FullPath))
                sb.AppendLine($"本机路径：{a.FullPath}");
            sb.AppendLine("```");
            sb.AppendLine(body);
            sb.AppendLine("```");
            sb.AppendLine("请基于上述完整内容回答，不要说「看不到文件」。");
        }

        // 二进制/其它类型：只交路径，让 Agent 用 list_dir/read_file/open_path/move 等工具
        foreach (var a in attachments.Where(x => x.Kind == ChatAttachmentKind.Other))
        {
            sb.AppendLine();
            sb.AppendLine($"【本机文件附件：{a.FileName}】");
            sb.AppendLine($"绝对路径：{a.FullPath ?? a.FileName}");
            sb.AppendLine("正文未预读（可能是二进制）。请用工具 read_file / open_path / list_dir 等按用户意图处理，不要说「看不到文件」。");
        }

        // 已嵌入 vision 的图片也附路径（若有），方便「把这张图挪到…」
        foreach (var a in attachments.Where(x =>
                     x.Kind == ChatAttachmentKind.Image
                     && x.ImageBytes is { Length: > 0 }
                     && !string.IsNullOrWhiteSpace(x.FullPath)))
        {
            sb.AppendLine($"图片本机路径：{a.FullPath}（文件名 {a.FileName}）");
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

    /// <summary>办公重任务：跳过天气/定位预取，避免关掉 tools 主路径。</summary>
    private static bool LooksLikeHeavyOfficeTask(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        string[] keys =
        [
            "整理", "批量", "移动", "重命名", "删除", "复制", "写入", "创建", "搜索文件", "列出",
            "写代码", "改代码", "写文件", "读文件", "执行命令", "powershell", "脚本",
            "总结网页", "抓网页", "搜索一下", "帮我处理", "办公", "计划", "todo",
            "batch", "rename", "move all", "organize",
        ];
        return keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
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
        // 聊天气泡不渲染 Markdown：去掉 **加粗** *斜体* 等，避免用户看到星号
        cleaned = Regex.Replace(cleaned, @"\*\*(.+?)\*\*", "$1");
        cleaned = Regex.Replace(cleaned, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");
        cleaned = Regex.Replace(cleaned, @"__(.+?)__", "$1");
        cleaned = Regex.Replace(cleaned, @"`([^`]+)`", "$1");
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
