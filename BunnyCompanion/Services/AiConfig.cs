namespace BunnyCompanion.Services;

/// <summary>
/// 内置接口与密钥（私人礼物用途，双击 EXE 即可使用）。
/// 主链：阶跃 Step Plan 多模态；兜底：Groq；最终：本地关键词。
/// </summary>
internal static class AiConfig
{
    // ---------- 阶跃 Step Plan（主选，多模态看桌面） ----------
    public const string StepBaseUrl = "https://api.stepfun.com/step_plan/v1";
    public const string StepApiKey = "18UA5cRvymm9sZPBsXYnr4MSo2YGAX2jKBP33l0rpd4R0kN2N63ECKNhRcfWBgFHg";
    /// <summary>旗舰多模态，适合看桌面、写代码、长对话。</summary>
    public const string StepPrimaryModel = "step-3.7-flash";
    /// <summary>更快更省 token 的备用阶跃模型。</summary>
    public const string StepFastModel = "step-3.5-flash";
    public const string StepEffort = "low";

    // ---------- Groq（主链失败后的在线兜底） ----------
    public const string GroqBaseUrl = "https://api.groq.com/openai/v1";
    public const string GroqApiKey = "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";
    /// <summary>Groq 文本主力（快）。</summary>
    public const string GroqTextModel = "llama-3.1-8b-instant";
    /// <summary>Groq 更强一点的文本模型。</summary>
    public const string GroqStrongModel = "llama-3.3-70b-versatile";
    /// <summary>Groq 视觉模型（看图兜底）。</summary>
    public const string GroqVisionModel = "meta-llama/llama-4-scout-17b-16e-instruct";

    public const int RequestTimeoutSeconds = 90;
    public const int MaxHistoryMessages = 16;
    public const int MaxReplyChars = 1800;
}
