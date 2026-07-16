namespace BunnyCompanion.Services;

/// <summary>
/// 进程内共享：记忆 JSON + 本地 agent.md。
/// </summary>
public static class CompanionRuntime
{
    private static CompanionMemoryService? _memory;
    private static LocalAgentMdStore? _agentMd;
    private static SkillPluginService? _skills;
    private static OfficePlanStore? _officePlan;

    public static CompanionMemoryService Memory
    {
        get => _memory ??= new CompanionMemoryService();
        set => _memory = value;
    }

    public static LocalAgentMdStore AgentMd
    {
        get => _agentMd ??= new LocalAgentMdStore();
        set => _agentMd = value;
    }

    public static SkillPluginService Skills
    {
        get => _skills ??= new SkillPluginService();
        set => _skills = value;
    }

    /// <summary>办公模式会话计划（plan_set / plan_tick）。</summary>
    public static OfficePlanStore OfficePlan
    {
        get => _officePlan ??= new OfficePlanStore();
        set => _officePlan = value;
    }

    /// <summary>结构化记忆落盘后同步刷新 agent.md 固定区。</summary>
    public static void SyncAgentMdFromMemory()
    {
        try
        {
            var structured = Memory.FormatForSystemPrompt();
            AgentMd.SyncStructured(structured);
        }
        catch
        {
            // ignore
        }
    }
}
