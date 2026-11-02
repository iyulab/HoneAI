using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HoneAI.Agents.Mcp;
using Microsoft.Extensions.AI;
using Xunit;

namespace HoneAI.Tests;

public class MloopToolPolicyTests
{
    private sealed class FakeTools(params AITool[] tools) : IMloopToolProvider
    {
        public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AITool>>(tools);
    }

    private static AIFunction Tool(string name, Action? onCall = null)
        => AIFunctionFactory.Create((string projectPath) => { onCall?.Invoke(); return "ran"; }, name);

    private static AIFunction Promote(Action onCall)
        => AIFunctionFactory.Create(
            (string projectPath, string? experimentId = null, bool? force = null, bool? latest = null) => { onCall(); return "promoted"; },
            "mloop_promote");

    private static async Task<IReadOnlyList<AITool>> Offered(MloopToolPolicy policy, params AITool[] tools)
        => await new PolicedMloopToolProvider(new FakeTools(tools), policy).GetToolsAsync();

    [Fact]
    public async Task WithoutAPolicy_EveryToolIsOffered()
    {
        var offered = await Offered(MloopToolPolicy.Everything, Tool("mloop_info"), Tool("mloop_train"), Tool("mloop_promote"));

        Assert.Equal(["mloop_info", "mloop_train", "mloop_promote"], offered.Select(t => t.Name));
    }

    [Fact]
    public async Task TheTrainingAndPromotionPreset_LeavesOutExactlyTheToolsThatTrainOrMoveProduction()
    {
        var names = new[]
        {
            "mloop_info", "mloop_analyze", "mloop_prep_plan", "mloop_features_select", "mloop_validate", "mloop_status", "mloop_evaluate",
            "mloop_train", "mloop_auto_train", "mloop_quick_start", "mloop_promote", "mloop_rollback",
        };

        var offered = await Offered(MloopToolPolicy.WithoutTrainingOrPromotion, names.Select(n => (AITool)Tool(n)).ToArray());

        Assert.Equal(
            ["mloop_info", "mloop_analyze", "mloop_prep_plan", "mloop_features_select", "mloop_validate", "mloop_status", "mloop_evaluate"],
            offered.Select(t => t.Name));
    }

    [Fact]
    public void ANameIsMatchedAsListedOrBehindAHostsPrefix_NeverByItsEnding()
    {
        var policy = MloopToolPolicy.Everything.Deny("mloop_train");

        Assert.False(policy.Permits("mloop_train"));
        Assert.False(policy.Permits("mloop__mloop_train"));
        Assert.False(policy.Permits("mloop:mloop_train"));
        Assert.True(policy.Permits("mloop_auto_train"));          // ends differently: a different tool
        Assert.True(policy.Permits("xmloop_train"));              // a longer word, not a prefix
    }

    [Fact]
    public async Task AnAllowList_AlsoLeavesOutATheToolAddedLater()
    {
        var offered = await Offered(MloopToolPolicy.AllowOnly("mloop_info"), Tool("mloop_info"), Tool("mloop_brand_new"));

        Assert.Equal(["mloop_info"], offered.Select(t => t.Name));
    }

    [Fact]
    public async Task AnArgumentThatIsForbidden_IsNotInTheSchemaTheModelSees_AndNotRequired()
    {
        var policy = MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force");

        var promote = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await Offered(policy, Promote(() => { }))));

        var properties = promote.JsonSchema.GetProperty("properties");
        Assert.False(properties.TryGetProperty("force", out _));
        Assert.True(properties.TryGetProperty("experimentId", out _));
        Assert.True(properties.TryGetProperty("latest", out _));
    }

    [Fact]
    public async Task ACallThatSetsAForbiddenArgument_IsRefusedWithAMessage_AndNothingRuns()
    {
        var ran = 0;
        var policy = MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force");
        var promote = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await Offered(policy, Promote(() => ran++))));

        var result = await promote.InvokeAsync(new AIFunctionArguments { ["projectPath"] = "p", ["force"] = true });

        Assert.Equal(0, ran);
        var message = Assert.IsType<string>(result);
        Assert.StartsWith("Refused:", message, StringComparison.Ordinal);
        Assert.Contains("'force'", message, StringComparison.Ordinal);
        Assert.Contains("mloop_promote", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusalAlsoCatchesTheArgumentAsJson()
    {
        var ran = 0;
        var policy = MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force");
        var promote = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await Offered(policy, Promote(() => ran++))));

        var result = await promote.InvokeAsync(new AIFunctionArguments { ["projectPath"] = "p", ["force"] = JsonSerializer.SerializeToElement(true) });

        Assert.Equal(0, ran);
        Assert.StartsWith("Refused:", Assert.IsType<string>(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AForbiddenArgumentThatIsFalseOrNull_IsNotSet_AndTheCallRuns(object? value)
    {
        var ran = 0;
        var policy = MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force");
        var promote = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await Offered(policy, Promote(() => ran++))));

        var result = await promote.InvokeAsync(new AIFunctionArguments { ["projectPath"] = "p", ["force"] = value, ["latest"] = true });

        Assert.Equal(1, ran);
        Assert.Equal("promoted", result?.ToString());
    }

    [Fact]
    public async Task ACallWithoutTheForbiddenArgument_Runs()
    {
        var ran = 0;
        var policy = MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force");
        var promote = Assert.IsAssignableFrom<AIFunction>(Assert.Single(await Offered(policy, Promote(() => ran++))));

        await promote.InvokeAsync(new AIFunctionArguments { ["projectPath"] = "p", ["experimentId"] = "exp-003" });

        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task AToolWithoutForbiddenArguments_IsOfferedAsItIs()
    {
        var info = Tool("mloop_info");

        var offered = await Offered(MloopToolPolicy.Everything.ForbidArguments("mloop_promote", "force"), info);

        Assert.Same(info, Assert.Single(offered));
    }

    [Fact]
    public async Task APolicyThatLeavesNoTool_IsRefused_NotAnAgentWithoutHands()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Offered(MloopToolPolicy.AllowOnly("mloop_nothing"), Tool("mloop_info")));

        Assert.Contains("no tool", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("yes", true)]
    [InlineData(0, false)]
    [InlineData(2, true)]
    public void WhatCountsAsSet(object? value, bool expected)
        => Assert.Equal(expected, PolicedMloopToolProvider.ArgumentGuardedFunction.IsSet(value));
}
