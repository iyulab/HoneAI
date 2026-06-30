using System;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Tests for <see cref="DualCheckRouter{TQuery,TResult}"/> — the lower-first,
/// confidence-gated escalation policy and the agreement/review signal it produces.
/// </summary>
public class DualCheckRouterTests
{
    private static ITracedPrediction<string> Pred(string value, ReasoningLayer layer, double confidence)
        => new TracedPrediction<string>(value,
            new PredictionProvenance { SourceLayer = layer, Confidence = confidence });

    private static Func<string, CancellationToken, Task<ITracedPrediction<string>>> Lower(ITracedPrediction<string> p)
        => (_, _) => Task.FromResult(p);

    [Fact]
    public async Task ConfidentLowerLayer_IsTrustedWithoutEscalating()
    {
        var escalated = false;
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.95)),
            (_, _, _) => { escalated = true; return Task.FromResult(Pred("NG", ReasoningLayer.Frontier, 0.9)); },
            confidenceThreshold: 0.7);

        var result = await router.RouteAsync("q");

        Assert.False(escalated);
        Assert.Equal("OK", result.Value);
        Assert.Equal(ReasoningLayer.AutoMl, result.Provenance.SourceLayer);
    }

    [Fact]
    public async Task LowConfidence_EscalatesAndAgreement_NoReview()
    {
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.40)),
            (_, _, _) => Task.FromResult(Pred("OK", ReasoningLayer.Frontier, 0.92)),
            confidenceThreshold: 0.7);

        var result = await router.RouteAsync("q");

        Assert.Equal("OK", result.Value);
        Assert.Equal(ReasoningLayer.Frontier, result.Provenance.SourceLayer);
        Assert.True(result.Provenance.Agreement);
        Assert.False(result.Provenance.RequiresReview);
    }

    [Fact]
    public async Task LowConfidence_EscalatesAndDisagreement_RequiresReview()
    {
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.40)),
            (_, _, _) => Task.FromResult(Pred("NG", ReasoningLayer.Frontier, 0.95)),
            confidenceThreshold: 0.7);

        var result = await router.RouteAsync("q");

        Assert.Equal("NG", result.Value);          // higher layer arbitrates the value
        Assert.False(result.Provenance.Agreement);
        Assert.True(result.Provenance.RequiresReview);
    }

    [Fact]
    public async Task EscalatedButHigherStillLowConfidence_RequiresReviewEvenIfAgree()
    {
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.30)),
            (_, _, _) => Task.FromResult(Pred("OK", ReasoningLayer.Frontier, 0.50)),
            confidenceThreshold: 0.7);

        var result = await router.RouteAsync("q");

        Assert.True(result.Provenance.Agreement);
        Assert.True(result.Provenance.RequiresReview);   // residual low confidence
    }

    [Fact]
    public async Task EscalateDelegate_ReceivesLowerResult()
    {
        ITracedPrediction<string>? seenLower = null;
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.20)),
            (_, lower, _) => { seenLower = lower; return Task.FromResult(Pred("NG", ReasoningLayer.Frontier, 0.9)); },
            confidenceThreshold: 0.7);

        await router.RouteAsync("q");

        Assert.NotNull(seenLower);
        Assert.Equal("OK", seenLower!.Value);
        Assert.Equal(0.20, seenLower.Provenance.Confidence);
    }

    [Fact]
    public async Task Escalation_PreservesEscalatingRoleInProvenance()
    {
        // 역할 플레이 ①: escalate 레이어가 스탬프한 provenance.Role이 router 재구성을 살아남아야 한다.
        var higher = new TracedPrediction<string>("NG",
            new PredictionProvenance
            {
                SourceLayer = ReasoningLayer.Frontier,
                Role = AgentRole.DomainExpert,
                Confidence = 0.95,
            });
        var router = new DualCheckRouter<string, string>(
            Lower(Pred("OK", ReasoningLayer.AutoMl, 0.40)),
            (_, _, _) => Task.FromResult<ITracedPrediction<string>>(higher),
            confidenceThreshold: 0.7);

        var result = await router.RouteAsync("q");

        Assert.Equal(AgentRole.DomainExpert, result.Provenance.Role);
    }

    [Fact]
    public void Ctor_RejectsOutOfRangeThreshold()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DualCheckRouter<string, string>(
            Lower(Pred("x", ReasoningLayer.AutoMl, 0.5)),
            (_, _, _) => Task.FromResult(Pred("y", ReasoningLayer.Frontier, 0.5)),
            confidenceThreshold: 1.5));
    }
}
