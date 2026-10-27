using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Learning;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class ClusterPropagationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly PropagationPolicy Policy = new(MinimumChecked: 5, MinimumAgreement: 0.8, WeightCap: 0.5);

    private static ConfirmedLabel<string> Representative(
        string decisionId = "rep", double trust = 0.9, double probability = 0.25, LabelOrigin origin = LabelOrigin.Drawn,
        LabelShare share = LabelShare.Training) => new()
        {
            DecisionId = decisionId,
            SliceKey = "s",
            Value = "danger",
            ConfirmedBy = "people",
            JudgedBy = "r-1",
            Kind = JudgeKind.Human,
            ConfirmedByVersion = "1",
            SnapshotVersion = "snap-1",
            Probability = probability,
            Trust = trust,
            TrustBasis = LabelTrustBasis.Measured,
            ConfirmedAt = Now.AddHours(-1),
            Share = share,
            Origin = origin,
        };

    private static PurityCheck Passing(int agreed = 9, int total = 10) =>
        ClusterPropagation.Assess([.. Enumerable.Repeat(true, agreed), .. Enumerable.Repeat(false, total - agreed)], Policy);

    [Fact]
    public void ThePurityCheck_NeedsEnoughCheckedDecisions_AndEnoughAgreement()
    {
        var few = ClusterPropagation.Assess([true, true, true, true], Policy);
        Assert.False(few.Passed);
        Assert.Contains("5 are required", few.Reason);

        var impure = ClusterPropagation.Assess([.. Enumerable.Repeat(true, 7), .. Enumerable.Repeat(false, 3)], Policy);
        Assert.False(impure.Passed);
        Assert.Equal(0.7, impure.Agreement, 12);
        Assert.Contains("80%", impure.Reason);

        var enough = ClusterPropagation.Assess([.. Enumerable.Repeat(true, 8), .. Enumerable.Repeat(false, 2)], Policy);
        Assert.True(enough.Passed);                                  // 0.8 meets a 0.8 requirement
        Assert.Equal((10, 8, 0.8), (enough.Checked, enough.Agreed, enough.Agreement));
        Assert.Null(enough.Reason);
    }

    [Fact]
    public void NothingIsAssumedAboutTheNumbers_APolicyOutOfRangeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterPropagation.Assess([true], new PropagationPolicy(0, 0.8, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterPropagation.Assess([true], new PropagationPolicy(1, 0, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterPropagation.Assess([true], new PropagationPolicy(1, 1.1, 0.5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterPropagation.Assess([true], new PropagationPolicy(1, 0.8, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClusterPropagation.Assess([true], new PropagationPolicy(1, 0.8, 1.5)));
    }

    [Fact]
    public void ASpreadLabel_TeachesAtTheRepresentativesTrustTimesPurity_CappedAndWithNoPi()
    {
        var rep = Representative(trust: 0.9, probability: 0.25);          // drawn at 0.25: its own weight would be 3.6
        var annotations = new Dictionary<string, string> { [MLoopFeedbackSink<string>.PredictionIdAnnotation] = "p-7" };

        var result = ClusterPropagation.Propagate(rep, "mate", "s", Passing(9, 10), Policy, Now, annotations);

        Assert.Equal(ConfirmationOutcome.Confirmed, result.Outcome);
        var label = Assert.IsType<ConfirmedLabel<string>>(result.Label);
        Assert.Equal("mate", label.DecisionId);
        Assert.Equal("danger", label.Value);
        Assert.Equal(LabelOrigin.Propagated, label.Origin);
        Assert.Equal("rep", label.PropagatedFrom);
        Assert.Equal(LabelShare.Training, label.Share);
        Assert.Equal(1.0, label.Probability);                              // not the representative's π — it stands for itself
        Assert.Equal(1.0, label.ShareProbability);
        Assert.Equal(0.9, label.Purity, 12);
        Assert.Equal(0.9, label.Trust);
        Assert.Equal(0.5, label.WeightCap);
        Assert.Equal(0.5, label.Weight, 12);                               // 0.9 × 0.9 = 0.81, capped at 0.5
        Assert.Equal("p-7", label.Annotations![MLoopFeedbackSink<string>.PredictionIdAnnotation]);
        Assert.Equal("people", label.ConfirmedBy);
        Assert.Equal("r-1", label.JudgedBy);
    }

    [Fact]
    public void BelowTheCap_TheWeightIsTrustTimesPurity()
    {
        var policy = new PropagationPolicy(5, 0.5, 1.0);
        var purity = ClusterPropagation.Assess([.. Enumerable.Repeat(true, 6), .. Enumerable.Repeat(false, 4)], policy);

        var label = ClusterPropagation.Propagate(Representative(trust: 0.8), "mate", "s", purity, policy, Now).Label!;

        Assert.Equal(0.8 * 0.6, label.Weight, 12);
    }

    [Fact]
    public void AClusterThatFailedItsCheck_SpreadsNothing()
    {
        var result = ClusterPropagation.Propagate(Representative(), "mate", "s", Passing(5, 10), Policy, Now);

        Assert.Equal(ConfirmationOutcome.NotAdmitted, result.Outcome);
        Assert.Null(result.Label);
        Assert.Contains("80%", result.Reason);
    }

    [Fact]
    public void ADecisionTheSplitHoldsOutForMeasuring_TakesNoSpreadLabel()
    {
        var split = new LabelSplit { EvaluationShare = 0.5 };
        var ids = Enumerable.Range(0, 200).Select(i => $"d-{i}").ToList();
        var held = ids.Where(id => split.Assign(id, "s").Share == LabelShare.Evaluation).ToList();
        var kept = ids.Where(id => split.Assign(id, "s").Share == LabelShare.Training).ToList();
        Assert.NotEmpty(held);
        Assert.NotEmpty(kept);

        foreach (var id in held)
        {
            var result = ClusterPropagation.Propagate(Representative(), id, "s", Passing(), Policy, Now, split: split);
            Assert.Equal(ConfirmationOutcome.NotAdmitted, result.Outcome);
        }
        foreach (var id in kept.Take(20))
        {
            var label = ClusterPropagation.Propagate(Representative(), id, "s", Passing(), Policy, Now, split: split).Label!;
            Assert.Equal(LabelShare.Training, label.Share);
            Assert.Equal(1.0, label.ShareProbability);                        // never divided by a share it does not stand for
        }
    }

    [Fact]
    public void WhatIsNotAnObservation_IsNotSpreadAgain_NorToItself_NorFromAHeldOutLabel()
    {
        var again = ClusterPropagation.Propagate(Representative(origin: LabelOrigin.Propagated), "mate", "s", Passing(), Policy, Now);
        var itself = ClusterPropagation.Propagate(Representative(), "rep", "s", Passing(), Policy, Now);
        var heldOut = ClusterPropagation.Propagate(Representative(share: LabelShare.Evaluation), "mate", "s", Passing(), Policy, Now);

        Assert.All([again, itself, heldOut], r => Assert.Equal(ConfirmationOutcome.NotALabel, r.Outcome));
    }

    [Fact]
    public void ALabelSomeoneJudged_AlwaysReplacesASpreadOne_AndIsNeverReplacedByIt()
    {
        var spread = ClusterPropagation.Propagate(Representative(trust: 1.0), "mate", "s", Passing(10, 10), Policy, Now).Label!;
        var judged = Representative("mate", trust: 0.5) with { ConfirmedAt = Now.AddDays(-3) };   // less trusted and older

        Assert.True(LabelConfirmation.Supersedes(spread, judged));
        Assert.False(LabelConfirmation.Supersedes(judged, spread));
    }

    [Fact]
    public void TheFeedbackSource_SaysItWasSpread_AndTheManifestCountsIt()
    {
        var spread = ClusterPropagation.Propagate(Representative(), "mate", "s", Passing(), Policy, Now).Label!;

        Assert.EndsWith("(propagated)", MLoopFeedbackSink<string>.SourceOf(spread));
        var manifest = TrainingSets.Manifest([Representative(), spread], split: null);
        Assert.Equal(1, manifest.ByOrigin[nameof(LabelOrigin.Propagated)]);
        Assert.Equal(1, manifest.ByOrigin[nameof(LabelOrigin.Drawn)]);
    }

    [Fact]
    public async Task AnUnsureResponse_IsReached_NotAFailureOfTheUpperTier_AndLeavesNoRecord()
    {
        var store = new InMemoryRecheckPoolStore();
        var start = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await store.AddAsync(new PoolItem
            {
                DecisionId = $"d{i}",
                TierId = "ml",
                SliceKey = "s",
                DecidedAt = start.AddMinutes(i),
                Probability = 0.1,
                InputRef = $"in/{i}",
                ExpiresAt = start.AddDays(7),
            }));
        }
        var policy = SettlementPolicy.Categorical(settleable: ["ok"], mustCatch: ["danger"]);
        var at = start.AddDays(1);

        PoolPresentation Shown(int i) => new($"d{i}", "ml", "s", $"in/{i}");
        await store.RespondAsync(PoolAnswers.Response(Shown(0), "people", "r-1", JudgeVerdict<string>.Answer("ok"), "ok", policy, at));
        await store.RespondAsync(PoolAnswers.Response(Shown(1), "people", "r-1", JudgeVerdict<string>.Abstain("pass"), "ok", policy, at));
        var unsure = PoolAnswers.Unsure(Shown(2), "people", "r-1", at);
        await store.RespondAsync(unsure);
        // d3 is never reached and has not expired: the window is not closed yet.

        var entries = new List<PoolEntry>();
        await foreach (var entry in store.ReadAsync(start.AddYears(-1), start.AddYears(1)))
            entries.Add(entry);
        Assert.False(RecheckPool.Account(entries, at).Closed);

        var account = RecheckPool.Account(entries, start.AddDays(8));
        var reach = Assert.Single(account.Reach);
        Assert.Equal((4, 3, 1), (reach.Pooled, reach.Reached, reach.Unsure));       // unsure is reached
        Assert.Equal(0.75, reach.Rate, 12);
        Assert.Equal(["d0", "d1"], account.Records.Select(r => r.DecisionId));         // and gives no record
        Assert.Equal(RecheckOutcome.UpperFailed, account.Records.Single(r => r.DecisionId == "d1").Outcome);   // only the pass is the upper tier's failure
        Assert.Equal(PoolResponseKind.Unsure, unsure.Kind);
        Assert.Null(unsure.Outcome);
    }
}
