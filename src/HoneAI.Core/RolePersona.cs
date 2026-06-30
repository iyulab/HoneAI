using System;
using System.Text;

namespace HoneAI;

/// <summary>
/// Builds the domain-neutral persona <c>Instructions</c> for a trust-loop role from a
/// <see cref="RoleContext"/> — the HoneAI-unique knowledge that ironhive's <c>IAgent</c>
/// has no notion of (design spec §1.3 "제조 역할 카탈로그 + 도메인 색 주입"). The skeleton
/// is domain-neutral with a <c>{Domain}</c> slot the consumer fills via
/// <see cref="RoleContext.Domain"/>; verdict vocabulary stays in the consumer/harness.
/// </summary>
/// <remarks>
/// The output is exactly what later becomes an ironhive <c>IAgent.Instructions</c> (stage ①
/// Core binding), so role execution is delegated, not re-invented (§1 "★ 재발명 금지").
/// Stage ① ships <see cref="AgentRole.DomainExpert"/> only; other roles' skeletons arrive
/// with their stages — Orchestrator/Translator/Operator ②, Inspector/Arbiter ③
/// (demand-driven, no speculative skeletons).
/// </remarks>
public static class RolePersona
{
    /// <summary>
    /// Renders the persona <c>Instructions</c> for <paramref name="context"/>'s role,
    /// with the domain colour injected.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The role has no persona skeleton yet (lands in a later stage — §2/§5).
    /// </exception>
    public static string BuildInstructions(RoleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Role switch
        {
            AgentRole.DomainExpert => BuildDomainExpert(context),
            _ => throw new NotSupportedException(
                $"Role '{context.Role}' has no persona skeleton yet; it lands in a later stage (design spec §2/§5)."),
        };
    }

    private static string BuildDomainExpert(RoleContext c)
    {
        var sb = new StringBuilder();
        sb.Append("당신은 ").Append(c.Domain).AppendLine(" 도메인의 전문가입니다.");
        if (!string.IsNullOrWhiteSpace(c.TaskType))
            sb.Append("아래는 ").Append(c.TaskType).AppendLine(" task의 AutoML 예측입니다.");
        sb.AppendLine("AutoML 예측을 도메인 타당성 관점에서 검토하고 최종 판정을 내리세요.");
        return sb.ToString();
    }
}
