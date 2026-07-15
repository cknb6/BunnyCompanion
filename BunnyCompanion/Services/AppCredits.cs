namespace BunnyCompanion.Services;

/// <summary>
/// 统一署名：本软件由传康KK开发。
/// </summary>
public static class AppCredits
{
    public const string Developer = "传康KK";
    public const string DeveloperAlias = "传康Kk（万能程序员）";
    public const string ProductName = "小申陪伴";
    public const string VersionLabel = "1.1";

    /// <summary>用户可见的一句话声明。</summary>
    public const string DevelopedByLine = "本软件由传康KK开发";

    public const string WeChat = "1837620622";
    /// <summary>交付/咨询邮箱（非 GitHub 提交邮箱）。</summary>
    public const string ContactEmail = "2040168455@qq.com";
    public const string Platforms = "咸鱼 / B站：万能程序员";
    /// <summary>开发者大号（GitHub 身份 / 绿点）。</summary>
    public const string GitHubMainUser = "1837620622";
    /// <summary>打包发布小号（Actions 额度，仓库托管 cknb6）。</summary>
    public const string GitHubBuildUser = "cknb6";
    public const string GitHubUser = GitHubMainUser;

    public static string AboutBody(int days, int interactions, int affection) =>
        $"""
        {ProductName} {VersionLabel}

        {DevelopedByLine}
        开发者：{Developer} / {DeveloperAlias}
        GitHub：{GitHubMainUser}（大号）· 构建发布：{GitHubBuildUser}

        已经陪伴 {days} 天
        互动 {interactions} 次 · 爱心值 {affection}

        中键或托盘「和我聊聊」打开多模态 Agent。
        在线不可用时自动切换本地中文陪伴。
        纪念日、设置与长期记忆保存在本机。

        联系：
        微信 {WeChat}
        邮箱 {ContactEmail}
        {Platforms}
        """;

    public static string ShortFooter => $"{DevelopedByLine} · 微信 {WeChat}";
}
