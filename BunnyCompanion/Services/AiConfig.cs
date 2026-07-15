namespace BunnyCompanion.Services;

/// <summary>
/// 接口与密钥配置。
/// 链路：阶跃 step-3.7-flash（主）→ OpenRouter 免费模型（在线兜底）→ 本地中文关键词（最终）。
/// OpenRouter 密钥按用户说明可长期使用。
/// </summary>
internal static class AiConfig
{
    // ---------- 阶跃 Step Plan（主选，多模态） ----------
    public const string StepBaseUrl = "https://api.stepfun.com/step_plan/v1";
    public const string StepApiKey = "18UA5cRvymm9sZPBsXYnr4MSo2YGAX2jKBP33l0rpd4R0kN2N63ECKNhRcfWBgFHg";
    /// <summary>旗舰多模态：聊天 + 看桌面。</summary>
    public const string StepModel = "step-3.7-flash";
    public const string StepEffort = "low";

    // ---------- OpenRouter 免费模型（在线兜底，永不过期 key） ----------
    public const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";
    public const string OpenRouterApiKey = "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";
    /// <summary>OpenRouter 建议带上应用标识（非鉴权必填，但利于限流识别）。</summary>
    public const string OpenRouterReferer = "https://github.com/cknb6/BunnyCompanion";
    public const string OpenRouterAppTitle = "XiaoShenCompanion";

    /// <summary>
    /// 免费文本兜底顺序（实测：openrouter/free 最稳；其余可能 429，按序尝试）。
    /// </summary>
    public static readonly string[] OpenRouterFreeTextModels =
    [
        "openrouter/free",
        "openai/gpt-oss-20b:free",
        "meta-llama/llama-3.3-70b-instruct:free",
        "meta-llama/llama-3.2-3b-instruct:free",
        "qwen/qwen3-next-80b-a3b-instruct:free",
        "nvidia/nemotron-nano-9b-v2:free",
        "cognitivecomputations/dolphin-mistral-24b-venice-edition:free",
    ];

    /// <summary>
    /// 免费视觉兜底顺序（看桌面时用；openrouter/free 与 gemma-4-26b 实测可用）。
    /// </summary>
    public static readonly string[] OpenRouterFreeVisionModels =
    [
        "openrouter/free",
        "google/gemma-4-26b-a4b-it:free",
        "google/gemma-4-31b-it:free",
        "nvidia/nemotron-nano-12b-v2-vl:free",
    ];

    public const int RequestTimeoutSeconds = 120;
    public const int MaxHistoryMessages = 20;
    /// <summary>聊天窗完整展示长文/代码，避免中途截断。</summary>
    public const int MaxReplyChars = 12000;
    /// <summary>单次文本附件塞进 prompt 的上限。</summary>
    public const int MaxAttachmentTextChars = 60000;
    /// <summary>单次对话最多 tool 往返轮数（防死循环）。</summary>
    public const int MaxToolRounds = 8;
    /// <summary>单次工具返回塞回模型的最大字符数。</summary>
    public const int MaxToolResultChars = 14000;
}
