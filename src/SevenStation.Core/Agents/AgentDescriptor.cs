namespace SevenStation.Core.Agents;

/// <summary>
/// 一个 Agent 支持哪些东西。客户端只按这张表决定显示什么，不按 Agent 的名字写分支，
/// 以后接新的 Agent 不用改界面。
/// </summary>
public sealed record AgentCapabilities
{
    /// <summary>会用工具，界面上能列工具步骤。</summary>
    public bool Tools { get; init; }

    /// <summary>有多档思考强度可调。</summary>
    public bool ThinkingLevels { get; init; }

    /// <summary>能在多个模型之间切换。</summary>
    public bool Models { get; init; }

    /// <summary>会报上下文用了多少。</summary>
    public bool ContextUsage { get; init; }

    /// <summary>能把上下文压一次。</summary>
    public bool Compaction { get; init; }

    /// <summary>有扩展包的概念。</summary>
    public bool Extensions { get; init; }

    /// <summary>有技能的概念。</summary>
    public bool Skills { get; init; }

    /// <summary>有斜杠命令。</summary>
    public bool Commands { get; init; }

    /// <summary>收附件与图片。</summary>
    public bool Attachments { get; init; }
}

/// <summary>
/// Host 报给客户端的 Agent 名片。<see cref="Info"/> 是给人看的键值行（版本、目录之类），
/// 由 Agent 自己决定写什么，界面照着排。
/// </summary>
public sealed record AgentDescriptor(
    string Id,
    string Name,
    string Version,
    bool Ready,
    AgentCapabilities Capabilities,
    IReadOnlyList<AgentInfoRow> Info);

/// <summary>名片上的一行：左边是标签，右边是值。</summary>
public sealed record AgentInfoRow(string Label, string Value);

/// <summary>
/// Host 里 Agent 的注册表。现在只有 Pi，以后接别的就往这里加，
/// Host 的调度与客户端都不用跟着改。
/// </summary>
public static class AgentRegistry
{
    public const string PiId = "pi";

    /// <summary>当前用来跑工作的 Agent。多 Agent 并存时这里要按工作来选择。</summary>
    public static string PrimaryId => PiId;

    public static IReadOnlyList<AgentDescriptor> Describe() => [PiAgent.Describe()];

    public static AgentDescriptor Primary =>
        Describe().FirstOrDefault(agent => agent.Id == PrimaryId) ?? Describe()[0];
}
