namespace HoneAI;

/// <summary>
/// A trust-loop role an agent plays when reasoning about a prediction. This is
/// <b>not</b> a new agent type — role execution is delegated to ironhive
/// (<c>IronHive.Core</c> <c>IAgent</c>/<c>IAgentOrchestrator</c>); this enum is the
/// <b>identifier of the trust-loop seat</b> a given <c>IAgent</c> occupies, used as the
/// role↔agent binding key and stamped onto <see cref="PredictionProvenance.Role"/>
/// ("어느 레이어의 어느 역할이 판정"). It parallels <see cref="ReasoningLayer"/> — the
/// layer says <i>how cheap/certain</i> the answer is; the role says <i>which expert
/// persona</i> produced it (design spec §3, AI-Manufacturing §3.4 LLM 역할표).
/// </summary>
public enum AgentRole
{
    /// <summary>현장 문제 → ML 문제 정의(task/label/metric). §3.4 번역.</summary>
    Translator,

    /// <summary>EDA→FE→알고리즘 선택→train 오케스트레이션(MLoop.MCP 도구). §3.4 오케스트레이션.</summary>
    Orchestrator,

    /// <summary>L3 escalate 판정 — task별 도메인 색을 입은 전문가 대리. §3.5 전문가 대리.</summary>
    DomainExpert,

    /// <summary>ML 예측 2중 체크. 플라이휠 ③.</summary>
    Inspector,

    /// <summary>결과 해석·설명·리포트. §3.4 운영.</summary>
    Operator,

    /// <summary>역할 불일치 시 중재(멀티에이전트). 플라이휠 ④.</summary>
    Arbiter,
}
