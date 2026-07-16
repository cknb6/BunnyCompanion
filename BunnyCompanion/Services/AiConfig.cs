namespace BunnyCompanion.Services;

/// <summary>
/// 接口与密钥配置。
/// 链路：阶跃 step-3.7-flash（主）→ OpenRouter 免费（极短兜底）→ 本地中文关键词。
/// </summary>
internal static class AiConfig
{
    // ---------- 阶跃 Step Plan（主选，多模态） ----------
    public const string StepBaseUrl = "https://api.stepfun.com/step_plan/v1";
    /// <summary>语音等 OpenAI 兼容接口在 /v1 下，与 step_plan 聊天路径不同。</summary>
    public const string StepAudioBaseUrl = "https://api.stepfun.com/v1";
    public const string StepApiKey = "18UA5cRvymm9sZPBsXYnr4MSo2YGAX2jKBP33l0rpd4R0kN2N63ECKNhRcfWBgFHg";
    /// <summary>旗舰多模态：聊天 + 看桌面。</summary>
    public const string StepModel = "step-3.7-flash";
    /// <summary>推理强度：low。过小 max_tokens 时 reasoning 会占满导致 content 为空。</summary>
    public const string StepEffort = "low";

    // ---------- 阶跃语音（仅女声；无效音色勿写，否则会失败） ----------
    public const string StepTtsModel = "step-tts-vivid";
    public const string StepAsrModel = "stepaudio-2.5-asr";
    /// <summary>知性姐姐（御姐感）。仅女声。</summary>
    public const string StepTtsVoice = "zhixingjiejie";
    public const double StepTtsSpeed = 1.05;
    /// <summary>女声回退链（禁止任何男声 id）。</summary>
    public static readonly string[] StepTtsFemaleVoices =
    [
        "zhixingjiejie", // 知性姐姐
        "wenrounvsheng", // 温柔甜美
        "qinqienvsheng", // 亲切
        "linjiajiejie",  // 邻家姐姐
    ];

    // ---------- OpenRouter 免费模型（在线兜底，只保留极少数，避免串行卡死） ----------
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string OpenRouterApiKey = "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";
    public const string OpenRouterReferer = "https://github.com/cknb6/BunnyCompanion";
    public const string OpenRouterAppTitle = "XiaoShenCompanion";

    /// <summary>文本兜底：只试 1 个，失败立刻本地（以前 7 个 × 120s 会卡死）。</summary>
    public static readonly string[] OpenRouterFreeTextModels =
    [
        "openrouter/free",
    ];

    /// <summary>视觉兜底：只试 1 个。</summary>
    public static readonly string[] OpenRouterFreeVisionModels =
    [
        "openrouter/free",
    ];

    /// <summary>HttpClient 全局上限（单请求另有更短的 CTS）。</summary>
    public const int RequestTimeoutSeconds = 55;
    /// <summary>阶跃单次请求超时（秒）。</summary>
    public const int StepRequestTimeoutSeconds = 40;
    /// <summary>OpenRouter 单次超时（秒）。</summary>
    public const int FallbackRequestTimeoutSeconds = 22;
    /// <summary>阶跃正文生成至少要这么多 max_tokens，否则 reasoning 会吃光导致 content 空。</summary>
    public const int StepMinMaxTokens = 800;
    public const int StepDefaultMaxTokens = 1600;
    public const int FallbackMaxTokens = 1200;

    public const int MaxHistoryMessages = 20;
    public const int MaxReplyChars = 12000;
    public const int MaxAttachmentTextChars = 60000;
    /// <summary>单次对话最多图片附件张数（多模态上下文）。</summary>
    public const int MaxImageAttachments = 4;
    /// <summary>单张图片压缩后软上限（字节），过大则再压 JPEG 质量。</summary>
    public const int MaxImageBytesSoft = 1_200_000;
    /// <summary>陪伴模式工具往返轮数。</summary>
    public const int MaxToolRounds = 6;
    /// <summary>办公 Agent 模式工具往返轮数（多步计划需要更长预算）。</summary>
    public const int MaxToolRoundsOffice = 14;
    public const int MaxToolResultChars = 12000;

    // ---------- 办公模式阶跃预算（对齐 Claude Code 式多步执行） ----------
    public const int StepOfficeMaxTokens = 2800;
    public const int StepOfficeMinMaxTokens = 1200;
    public const int StepOfficeTimeoutSeconds = 60;
    /// <summary>办公模式推理强度：medium，兼顾工具规划与速度。</summary>
    public const string StepOfficeEffort = "medium";
}
