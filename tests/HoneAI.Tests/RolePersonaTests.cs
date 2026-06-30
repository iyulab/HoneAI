using System;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Pins the DomainExpert persona skeleton (stage ①): domain-neutral text with the
/// consumer's <see cref="RoleContext.Domain"/> injected, the task adapted when present,
/// and not-yet-staged roles surfaced as an explicit <see cref="NotSupportedException"/>
/// rather than a silent empty persona (design spec §2/§3/§5).
/// </summary>
public class RolePersonaTests
{
    [Fact]
    public void DomainExpert_InjectsConsumerDomainColour()
    {
        var instructions = RolePersona.BuildInstructions(
            new RoleContext { Role = AgentRole.DomainExpert, Domain = "전해탈지 공정 이상탐지" });

        Assert.Contains("전해탈지 공정 이상탐지", instructions);
        Assert.Contains("전문가", instructions);
    }

    [Fact]
    public void DomainExpert_AdaptsToTaskWhenPresent()
    {
        var withTask = RolePersona.BuildInstructions(
            new RoleContext { Role = AgentRole.DomainExpert, Domain = "용접", TaskType = "anomaly" });
        var withoutTask = RolePersona.BuildInstructions(
            new RoleContext { Role = AgentRole.DomainExpert, Domain = "용접" });

        Assert.Contains("anomaly", withTask);
        Assert.DoesNotContain("anomaly", withoutTask);
    }

    [Theory]
    [InlineData(AgentRole.Orchestrator)]
    [InlineData(AgentRole.Translator)]
    [InlineData(AgentRole.Operator)]
    [InlineData(AgentRole.Inspector)]
    [InlineData(AgentRole.Arbiter)]
    public void NotYetStagedRole_ThrowsRatherThanEmptyPersona(AgentRole role)
    {
        // Demand-driven: roles land with their stage; an unbuilt role is a loud error.
        Assert.Throws<NotSupportedException>(() =>
            RolePersona.BuildInstructions(new RoleContext { Role = role, Domain = "x" }));
    }

    [Fact]
    public void BuildInstructions_NullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => RolePersona.BuildInstructions(null!));
    }
}
