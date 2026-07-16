namespace BunnyCompanion.Services;

/// <summary>
/// 统一署名：本软件由传康KK开发。
/// </summary>
public static class AppCredits
{
    public const string Developer = "传康KK";
    public const string DeveloperAlias = "传康Kk（万能程序员）";
    public const string ProductName = "小申陪伴";
    /// <summary>版本号：单一来源为 csproj 的 &lt;Version&gt;，这里运行时读取程序集版本，避免多处硬编码不一致。</summary>
    public static string VersionLabel { get; } = ComputeVersionLabel();

    private static string ComputeVersionLabel()
    {
        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            // 程序集版本形如 1.2.0.0，去掉末尾的 .0 更友好
            return v is { } ver ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0";
        }
        catch
        {
            return "1.0";
        }
    }

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

        联系：
        微信 {WeChat}
        邮箱 {ContactEmail}
        {Platforms}
        """;

    public static string ShortFooter => $"{DevelopedByLine} · 微信 {WeChat}";
}
