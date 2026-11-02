using System;
using HoneAI;
using HoneAI.Learning;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class PoolAnswerRoutingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "exp-3", CostRank = 1 };
    private static readonly Tier People = new() { Id = "people", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);

    private static PoolPresentation Shown(PoolPurpose purpose, string id = "d1")
        => new(id, "ml", "slice-a", $"in/{id}", purpose);

    private static RoutedAnswer<string> Route(
        PoolPurpose purpose, PoolJudgment<string> judgment, PoolAnswerBasis<string> basis, string reviewer = "r-1", Tier? checker = null, string id = "d1")
        => PoolAnswerRouting.Route(Shown(purpose, id), checker ?? People, reviewer, judgment, basis, Policy, Now);

    private static RecheckResult<string> Earlier(string value, string reviewer, string decisionId = "d1", Tier? tier = null)
        => new(decisionId, tier ?? People, JudgeVerdict<string>.Answer(value, judgedBy: reviewer), Now.AddHours(-2));

    [Fact]
    public void AnAnsweredRecheck_GoesToThePool_AndIsTheAnswerALabelIsConfirmedFrom()
    {
        var routed = Route(PoolPurpose.Recheck, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRecheck("ok"));

        Assert.Equal(PoolResponseKind.Answered, routed.Response.Kind);
        Assert.Equal(RecheckOutcome.Agreed, routed.Response.Outcome);
        Assert.False(routed.Response.UpperMustCatch);
        Assert.Equal("people", routed.Response.CheckedBy);
        Assert.Equal("r-1", routed.Response.ReviewerKey);
        Assert.NotNull(routed.Result);
        Assert.Equal("ok", routed.Result!.Verdict.Value);
        Assert.Null(routed.Plant);
        Assert.Null(routed.Repeat);
        Assert.Null(routed.DoubleCheck);

        // The stamp says who answered: the person within the rung, not the rung alone.
        var confirming = Assert.IsAssignableFrom<ITracedPrediction<string>>(routed.Confirming);
        Assert.Equal("ok", confirming.Value);
        Assert.Equal("people", confirming.Provenance.TierId);
        Assert.Equal("1", confirming.Provenance.TierVersion);
        Assert.Equal(JudgeKind.Human, confirming.Provenance.Kind);
        Assert.Equal("r-1", confirming.Provenance.JudgedBy);
        Assert.Equal("slice-a", confirming.Provenance.SliceKey);
    }

    [Fact]
    public void ARecheckThatDisagreesOnAMustCatchValue_IsRecordedAsBoth()
    {
        var routed = Route(PoolPurpose.Recheck, PoolJudgment<string>.Answer("danger"), PoolAnswerBasis<string>.ForRecheck("ok"));

        Assert.Equal(RecheckOutcome.Disagreed, routed.Response.Outcome);
        Assert.True(routed.Response.UpperMustCatch);
    }

    [Fact]
    public void APass_IsTheUpperTiersFailure_AndConfirmsNoLabel()
    {
        var routed = Route(PoolPurpose.Recheck, PoolJudgment<string>.Pass("too hard"), PoolAnswerBasis<string>.ForRecheck("ok"));

        Assert.Equal(PoolResponseKind.Skipped, routed.Response.Kind);
        Assert.NotNull(routed.Result);
        Assert.False(routed.Result!.Verdict.Answered);
        Assert.Equal("r-1", routed.Result.Verdict.JudgedBy);
        Assert.Null(routed.Confirming);
    }

    [Fact]
    public void ACouldNotTell_IsNotAPass_HasNoJudgmentToPair_AndConfirmsNoLabel()
    {
        foreach (var (purpose, basis) in new (PoolPurpose, PoolAnswerBasis<string>)[]
        {
            (PoolPurpose.Recheck, PoolAnswerBasis<string>.ForRecheck("ok")),
            (PoolPurpose.Plant, PoolAnswerBasis<string>.ForPlant("ok")),
            (PoolPurpose.Repeat, PoolAnswerBasis<string>.ForRepeat(Earlier("ok", "r-1"))),
            (PoolPurpose.SecondOpinion, PoolAnswerBasis<string>.ForSecondOpinion(Earlier("ok", "r-2"))),
        })
        {
            var routed = Route(purpose, PoolJudgment<string>.CouldNotTell(), basis);

            Assert.Equal(PoolResponseKind.Unsure, routed.Response.Kind);
            Assert.NotEqual(PoolResponseKind.Skipped, routed.Response.Kind);
            Assert.Null(routed.Result);
            Assert.Null(routed.Plant);
            Assert.Null(routed.Repeat);
            Assert.Null(routed.DoubleCheck);
            Assert.Null(routed.Confirming);
        }
    }

    [Theory]
    [InlineData("ok", PlantOutcome.Correct)]
    [InlineData("danger", PlantOutcome.Wrong)]
    public void AnAnsweredPlant_MeasuresTheJudge_AndIsNeverALabel(string answer, PlantOutcome expected)
    {
        var routed = Route(PoolPurpose.Plant, PoolJudgment<string>.Answer(answer), PoolAnswerBasis<string>.ForPlant("ok", "expert"), id: "plant-7");

        var plant = Assert.IsType<PlantRecord>(routed.Plant);
        Assert.Equal(expected, plant.Outcome);
        Assert.Equal("plant-7", plant.PlantId);
        Assert.Equal("people", plant.TierId);
        Assert.Equal("r-1", plant.JudgedBy);
        Assert.Equal("slice-a", plant.SliceKey);
        Assert.Equal("expert", plant.TruthSource);
        Assert.Null(routed.Confirming);
        Assert.Null(routed.Repeat);
    }

    [Fact]
    public void APassedPlant_IsAFailureOfTheJudge()
    {
        var routed = Route(PoolPurpose.Plant, PoolJudgment<string>.Pass(), PoolAnswerBasis<string>.ForPlant("ok"));

        Assert.Equal(PlantOutcome.Failed, Assert.IsType<PlantRecord>(routed.Plant).Outcome);
    }

    [Theory]
    [InlineData("ok", true)]
    [InlineData("danger", false)]
    public void ARepeat_MeasuresTheJudgesConsistencyWithItself(string second, bool consistent)
    {
        var routed = Route(PoolPurpose.Repeat, PoolJudgment<string>.Answer(second), PoolAnswerBasis<string>.ForRepeat(Earlier("ok", "r-1")));

        var repeat = Assert.IsType<RepeatRecord>(routed.Repeat);
        Assert.Equal(consistent, repeat.Consistent);
        Assert.Equal(new JudgeIdentity("people", "r-1"), repeat.Judge);
        Assert.Null(routed.Confirming);
        Assert.Null(routed.DoubleCheck);
    }

    [Fact]
    public void ARepeatByAnotherJudge_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            Route(PoolPurpose.Repeat, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRepeat(Earlier("ok", "r-2"))));
    }

    [Fact]
    public void ASecondOpinion_MeasuresTheTwoJudgesTogether()
    {
        var routed = Route(PoolPurpose.SecondOpinion, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForSecondOpinion(Earlier("ok", "r-2")));

        var record = Assert.IsType<DoubleCheckRecord>(routed.DoubleCheck);
        Assert.True(record.Agreed);
        Assert.Null(routed.Repeat);
        Assert.Null(routed.Confirming);
    }

    [Fact]
    public void ASecondOpinionThatDiffers_IsADisagreement()
    {
        var routed = Route(PoolPurpose.SecondOpinion, PoolJudgment<string>.Answer("danger"), PoolAnswerBasis<string>.ForSecondOpinion(Earlier("ok", "r-2")));

        Assert.False(Assert.IsType<DoubleCheckRecord>(routed.DoubleCheck).Agreed);
    }

    [Fact]
    public void ASecondOpinionByTheSameJudge_OrOfAnotherDecision_IsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            Route(PoolPurpose.SecondOpinion, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForSecondOpinion(Earlier("ok", "r-1"))));
        Assert.Throws<ArgumentException>(() =>
            Route(PoolPurpose.SecondOpinion, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForSecondOpinion(Earlier("ok", "r-2", decisionId: "other"))));
    }

    [Fact]
    public void ABasisForAnotherPurposeThanTheItemWasShownFor_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Route(PoolPurpose.Plant, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRecheck("ok")));

        Assert.Contains(nameof(PoolPurpose.Plant), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PoolPurpose.Recheck), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheResponse_IsExactlyWhatThePoolAnswersFunctionMakes()
    {
        var shown = Shown(PoolPurpose.Recheck);
        var verdict = JudgeVerdict<string>.Answer("danger", judgedBy: "r-1");

        var routed = Route(PoolPurpose.Recheck, PoolJudgment<string>.Answer("danger"), PoolAnswerBasis<string>.ForRecheck("ok"));

        Assert.Equal(PoolAnswers.Response(shown, People.Id, "r-1", verdict, "ok", Policy, Now), routed.Response);
        Assert.Equal(PoolAnswers.Unsure(shown, People.Id, "r-1", Now),
            Route(PoolPurpose.Recheck, PoolJudgment<string>.CouldNotTell(), PoolAnswerBasis<string>.ForRecheck("ok")).Response);
    }

    [Fact]
    public void TheJudgeIdentityOfTheRepeatRecord_IsTheOneTheLabelConfirmationMatchesTheAnswerBy()
    {
        // Label confirmation finds a judge's own repeat by (tier, JudgedBy): the routed answer and the routed repeat must agree on it.
        var answer = Route(PoolPurpose.Recheck, PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRecheck("ok"));
        var repeat = Route(PoolPurpose.Repeat, PoolJudgment<string>.Answer("danger"), PoolAnswerBasis<string>.ForRepeat(Earlier("ok", "r-1")));

        var stamp = answer.Confirming!.Provenance;
        Assert.Equal(new JudgeIdentity(stamp.TierId!, stamp.JudgedBy), repeat.Repeat!.Judge);
    }

    [Fact]
    public void ARoutedRecheckAnswer_BecomesALabelWeightedByTheWindowsDraw()
    {
        // The tier ML settled d1 and the decision was pooled with π = 0.5; half of the window's pooled items were reached,
        // so the answer stands for 1 / (0.5 × 0.5) = 4 such decisions.
        var item = new PoolItem
        {
            DecisionId = "d1",
            TierId = "ml",
            SliceKey = "slice-a",
            DecidedAt = Now.AddDays(-1),
            Probability = 0.5,
            InputRef = "in/d1",
            ExpiresAt = Now.AddDays(6),
        };
        var draw = PoolAnswers.ReachedDraw(item, new PoolReach("ml", "slice-a", Pooled: 8, Reached: 4));
        var decision = new PredictionProvenance
        {
            Kind = Ml.Kind,
            TierId = Ml.Id,
            SliceKey = "slice-a",
            SnapshotVersion = "snap-1",
            Sample = draw,
        };
        var routed = Route(PoolPurpose.Recheck, PoolJudgment<string>.Answer("minor"), PoolAnswerBasis<string>.ForRecheck("ok"));

        var result = LabelConfirmation.Decide("d1", decision, [routed.Confirming!], Ml, [Ml, People], Policy, _ => null, Now);

        Assert.Equal(ConfirmationOutcome.Confirmed, result.Outcome);
        var label = Assert.IsType<ConfirmedLabel<string>>(result.Label);
        Assert.Equal("minor", label.Value);
        Assert.Equal("people", label.ConfirmedBy);
        Assert.Equal(0.25, label.Probability, 12);
        Assert.Equal(4.0, label.Weight, 12);
    }

    [Fact]
    public void AMissingAnswerOrBasisValue_IsRefusedAtTheDoor()
    {
        Assert.Throws<ArgumentNullException>(() => PoolJudgment<string>.Answer(null!));
        Assert.Throws<ArgumentNullException>(() => PoolAnswerBasis<string>.ForRecheck(null!));
        Assert.Throws<ArgumentNullException>(() => PoolAnswerBasis<string>.ForPlant(null!));
        Assert.Throws<ArgumentNullException>(() => PoolAnswerBasis<string>.ForRepeat(null!));
        Assert.Throws<ArgumentException>(() =>
            PoolAnswerRouting.Route(Shown(PoolPurpose.Recheck), People, " ", PoolJudgment<string>.Answer("ok"), PoolAnswerBasis<string>.ForRecheck("ok"), Policy, Now));
    }
}
