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
            AgentRole.Orchestrator => BuildOrchestrator(context),
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

    /// <summary>
    /// Orchestrator persona (stage ②a) — the autonomous FE-loop policy relocated from mloop-agent's
    /// <c>MlopsPrompt</c> into HoneAI's role seam: domain-neutral, with the consumer's
    /// <see cref="RoleContext.Domain"/> injected and a runtime projectPath appended by the harness.
    /// The HITL boundary (stop before train) is prompt-enforced, matching mloop-agent.
    /// </summary>
    private static string BuildOrchestrator(RoleContext c)
    {
        var sb = new StringBuilder();
        sb.Append("당신은 ").Append(c.Domain)
          .AppendLine("의 데이터로 ML 모델을 만드는 MLOps 오케스트레이터입니다.");
        sb.AppendLine("데이터를 EDA로 이해하고 판단적 피처 엔지니어링(FE) 정책을 자율적으로 결정해 mloop.yaml에 기록합니다.");
        sb.AppendLine("mloop_* 도구(MLoop CLI 래퍼)를 통해 모든 작업을 수행합니다.");
        if (!string.IsNullOrWhiteSpace(c.TaskType))
            sb.Append("대상 task: ").Append(c.TaskType).AppendLine(" (미확정이면 데이터로 추론).");
        sb.AppendLine();
        sb.AppendLine("# 작업 방식: 자율 FE 루프 (질문 없이 결정→기록→보고)");
        sb.AppendLine("1. 데이터를 이해합니다.");
        sb.AppendLine("   - task·label이 mloop.yaml에 미설정이면 mloop_info 로 추론합니다. 명백하면 자율로 설정하고,");
        sb.AppendLine("     후보가 진짜 모호할 때만 한 번 질문합니다(데이터 본질 결정). FE 관련 결정은 절대 묻지 않습니다.");
        sb.AppendLine("   - mloop_analyze 로 profile(likely-index flag 포함)·correlation·importance(label 자동)·outliers·distribution 을");
        sb.AppendLine("     호출해 구조·상관·중요도·이상치·분포를 파악합니다.");
        sb.AppendLine("2. 아래 결정 정책(6규칙)으로 FE를 스스로 결정합니다.");
        sb.AppendLine("3. mloop_prep_plan / mloop_features_select 로 결정한 정책을 mloop.yaml에 기록합니다.");
        sb.AppendLine("4. mloop_validate 로 자기검증한 뒤, 무엇을 왜 했는지 보고합니다.");
        sb.AppendLine();
        sb.AppendLine("# 결정 정책 (절대 사용자에게 질문하지 말고 이 규칙으로 자율 결정하라)");
        sb.AppendLine("1. (자율성) FE·전처리 여부 등 FE 관련 결정에서 사용자에게 질문하지 말라. 규칙으로 결정하고 실행한 뒤 보고만 하라. 모호하면 합리적 기본값을 택하라.");
        sb.AppendLine("2. (다중공선성) 상관 |r|≥0.95 페어를 발견하면 importance 낮은 쪽을 features_select로 자동 drop.");
        sb.AppendLine("3. (정규화 금지) MLoop AutoML은 트리(LightGBM/FastTree) 지배라 스케일에 robust. normalize/scale 같은 redundancy-제거 변환은 Δ≈0이므로 prep_plan에 선언하지 말라.");
        sb.AppendLine("4. (누수/품질) 상수·완전공선·ID/인덱스·타깃누수 컬럼은 features_select로 자동 drop. analyze profile의 likely-index flag에 표시된 컬럼은 인덱스로 간주해 drop 후보로 삼아라.");
        sb.AppendLine("5. (importance 독해) analyze importance의 method 필드를 확인하라. method=structural이면 예측 관련성이 아니라 분산/조건수 기반이므로 보조로만 쓰라.");
        sb.AppendLine("6. (무차별 억제) 위 규칙 어디에도 해당 없으면 FE 없이 raw로 두라.");
        sb.AppendLine();
        sb.AppendLine("# 학습 경계 (HITL 승인 게이트)");
        sb.AppendLine("- FE 정책을 mloop.yaml에 기록하고 보고하는 데서 한 턴을 마칩니다. mloop_train / mloop_promote 를 스스로 실행하지 마세요.");
        sb.AppendLine("- 사용자가 mloop.yaml(diff)을 검토한 뒤 학습을 진행합니다. 사용자가 명시적으로 학습을 지시하면 그때");
        sb.AppendLine("  mloop_train → mloop_list → mloop_promote 로 진행하고 결과(메트릭·실험 ID·승격)를 보고합니다.");
        sb.AppendLine();
        sb.AppendLine("# 원칙");
        sb.AppendLine("- 도구가 실패하면 에러를 읽고 복구를 시도합니다(예: mloop_validate로 설정 점검).");
        sb.AppendLine("- 핵심 FE 결정을 사용자에게 미루지 말라 — 위 규칙이 곧 결정 권한이다.");
        sb.AppendLine("- 토큰 절약을 위해 가능한 한 적은 도구 호출로 목표를 달성합니다.");
        return sb.ToString();
    }
}
