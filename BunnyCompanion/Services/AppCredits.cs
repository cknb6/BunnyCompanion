namespace BunnyCompanion.Services;

/// <summary>
/// 统一署名：本软件由传康KK开发。
/// </summary>
public static class AppCredits
{
    public const string Developer = "传康KK";
    public const string DeveloperAlias = "传康Kk（万能程序员）";
    public const string ProductName = "小申陪伴";
    /// <summary>版本号：优先 FileVersion（含 CI 补丁号），便于自动更新比较。</summary>
    public static string VersionLabel { get; } = AppUpdateService.FormatVersion(AppUpdateService.GetLocalVersion());

    public const string UpdateRepo = "cknb6/BunnyCompanion";

    /// <summary>用户可见的一句话声明。</summary>
    public const string DevelopedByLine = "本软件由传康KK开发";

    public const string WeChat = "1837620622";
    /// <summary>用户咨询邮箱。</summary>
    public const string ContactEmail = "2040168455@qq.com";
    public const string Platforms = "咸鱼 / B站：万能程序员";
    public const string GitHubUser = "1837620622";

    public static string AboutBody(int days, int interactions, int affection) =>
        $"""
        {ProductName} {VersionLabel}

        {DevelopedByLine}
        开发者：{Developer} / {DeveloperAlias}
        GitHub：{GitHubUser}

        已经陪伴 {days} 天
        互动 {interactions} 次 · 爱心值 {affection}

        中键或托盘「和我聊聊」打开多模态 Agent。
        在线不可用时自动切换本地中文陪伴。
        纪念日、设置与长期记忆保存在本机。
        自动更新：GitHub {UpdateRepo}，下载后强制 SHA256 校验。

        联系：
        微信 {WeChat}
        邮箱 {ContactEmail}
        {Platforms}
        """;

    public static string ShortFooter => $"{DevelopedByLine} · 微信 {WeChat}";
}
