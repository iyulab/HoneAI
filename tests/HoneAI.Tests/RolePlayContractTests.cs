using System.Collections.Generic;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the role-play contract shapes (design spec §3): <see cref="AgentRole"/> as the
/// trust-loop seat identifier, <see cref="RoleContext"/> as the opaque domain-colour slot,
/// and <see cref="PredictionProvenance.Role"/> as a first-class provenance field. These
/// assert the declared contract is consumable; role <i>execution</i> (ironhive IAgent
/// binding) lands in stage ① Core, not here.
/// </summary>
public class RolePlayContractTests
{
    [Fact]
    public void AgentRole_CoversTheSixVisionRoles()
    {
        // §3.4 LLM 역할표 + 멀티에이전트 — the six declared seats are all present.
        var roles = new[]
        {
            AgentRole.Translator, AgentRole.Orchestrator, AgentRole.DomainExpert,
            AgentRole.Inspector, AgentRole.Operator, AgentRole.Arbiter,
        };

        Assert.Equal(6, roles.Length);
        Assert.Equal(roles.Length, System.Enum.GetValues<AgentRole>().Length);
    }

    [Fact]
    public void RoleContext_CarriesRoleAndOpaqueDomainColour()
    {
        // Domain is consumer-injected and opaque to the middleware (Annotations pattern).
        var ctx = new RoleContext
        {
            Role = AgentRole.DomainExpert,
            Domain = "전해탈지 공정 이상탐지",
            TaskType = "anomaly",
        };

        Assert.Equal(AgentRole.DomainExpert, ctx.Role);
        Assert.Equal("전해탈지 공정 이상탐지", ctx.Domain);
        Assert.Equal("anomaly", ctx.TaskType);
    }

    [Fact]
    public void RoleContext_TaskTypeIsOptional()
    {
        // Task-agnostic roles bind without a task hint.
        var ctx = new RoleContext { Role = AgentRole.Arbiter, Domain = "general" };

        Assert.Null(ctx.TaskType);
    }

    [Fact]
    public void Provenance_StampsRoleAlongsideLayer()
    {
        // "어느 레이어의 어느 역할이 판정" — role and layer travel together.
        var p = new PredictionProvenance
        {
            SourceLayer = ReasoningLayer.Frontier,
            Role = AgentRole.DomainExpert,
            Confidence = 0.5,
            Rationale = "domain-expert escalation",
        };

        Assert.Equal(ReasoningLayer.Frontier, p.SourceLayer);
        Assert.Equal(AgentRole.DomainExpert, p.Role);
    }

    [Fact]
    public void Provenance_RoleIsNullWhenNoRoleBound()
    {
        // Single-layer answers with no role binding leave Role null (back-compat).
        var p = new PredictionProvenance { SourceLayer = ReasoningLayer.AutoMl, Confidence = 0.9 };

        Assert.Null(p.Role);
    }
}
