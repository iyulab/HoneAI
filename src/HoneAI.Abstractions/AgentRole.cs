namespace HoneAI;

/// <summary>
/// A trust-loop role an agent plays when reasoning about a prediction. This is
/// <b>not</b> a new agent type — role execution is delegated to an agent framework
/// (e.g. IronHive's <c>IAgent</c>/<c>IAgentOrchestrator</c>); this enum is the
/// <b>identifier of the trust-loop seat</b> a given agent occupies, used as the
/// role↔agent binding key and stamped onto <see cref="PredictionProvenance.Role"/>
/// ("어느 레이어의 어느 역할이 판정"). It parallels <see cref="ReasoningLayer"/> — the
/// layer says <i>how cheap/certain</i> the answer is; the role says <i>which expert
/// persona</i> produced it.
/// </summary>
public enum AgentRole
{
    /// <summary>현장 문제 → ML 문제 정의(task/label/metric).</summary>
    Translator,

    /// <summary>EDA→FE→알고리즘 선택→train 오케스트레이션(MLoop MCP 도구).</summary>
    Orchestrator,

    /// <summary>L3 escalate 판정 — task별 도메인 색을 입은 전문가 대리.</summary>
    DomainExpert,

    /// <summary>ML 예측 2중 체크.</summary>
    Inspector,

    /// <summary>결과 해석·설명·리포트.</summary>
    Operator,

    /// <summary>역할 불일치 시 중재(멀티에이전트).</summary>
    Arbiter,
}
