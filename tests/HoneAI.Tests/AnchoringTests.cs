using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class AnchoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Teacher = new() { Id = "teacher", Kind = JudgeKind.Llm, Version = "p-3", CostRank = 2, AcceptsHints = true };
    private static readonly Tier Closed = new() { Id = "closed", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
    private static readonly ISettlementPolicy<string> Policy = SettlementPolicy.Categorical(new[] { "OK", "LOW" }, new[] { "HIGH" });

    // Input = (id, what the teacher answers blind). Shown the lower answer, this teacher always takes it.
    private sealed record Case(string Id, string BlindAnswer);

    private static TieredRouter<Case, string> Router(List<JudgeContext>? seen = null)
        => new(
            [Judge.Constant<Case, string>(Ml, "OK"),
             Judge.From<Case, string>(Teacher, (input, context, _) =>
             {
                 seen?.Add(context);
                 var answer = context.Hints?.GetValueOrDefault(JudgeContext.LowerAnswerHint) ?? input.BlindAnswer;
                 return ValueTask.FromResult(JudgeVerdict<string>.Answer(answer));
             }),
             Judge.Constant<Case, string>(Closed, "LOW")],
            Policy, SliceKey.From<Case>("k", _ => "s"), c => c.Id, () => null,
            new TieredRouterOptions { Clock = () => Now });

    [Theory]
    [InlineData(0, 10, 0.001953125)]   // 2 · ½¹⁰
    [InlineData(5, 5, 1.0)]
    [InlineData(1, 0, 1.0)]
    public void McNemarExactP_IsTwiceTheSmallerTail(int b, int c, double expected)
        => Assert.Equal(expected, AnchoringMeasure.McNemarExactP(b, c)!.Value, 9);

    [Fact]
    public void McNemarExactP_WithoutAMove_IsUnknown()
        => Assert.Null(AnchoringMeasure.McNemarExactP(0, 0));

    [Fact]
    public async Task AShownRecheck_CarriesTheLowerAnswer_AndIsMarkedShown()
    {
        var seen = new List<JudgeContext>();

        var shown = await Router(seen).RecheckShownAsync(new Case("d1", "HIGH"), "d1", "teacher", "OK");

        var context = Assert.Single(seen);
        Assert.True(context.IsRecheck);
        Assert.Equal("OK", context.Hints![JudgeContext.LowerAnswerHint]);
        Assert.True(shown.Shown);
        Assert.Equal("OK", shown.Verdict.Value);
    }

    [Fact]
    public async Task ATierThatTakesNoHints_CannotBeShown()
        => await Assert.ThrowsAsync<ArgumentException>(() => Router().RecheckShownAsync(new Case("d1", "OK"), "d1", "closed", "OK"));

    [Fact]
    public async Task TheLedgerAndThePlants_RefuseAShownJudgment()
    {
        var router = Router();
        var shown = await router.RecheckShownAsync(new Case("d1", "HIGH"), "d1", "teacher", "OK");

        Assert.Throws<ArgumentException>(() => PlantRecords.From(shown, "HIGH", "s", Policy));
        var decision = new RoutedDecision<string>("d1",
            new TracedPrediction<string>("OK", new PredictionProvenance { Kind = JudgeKind.Ml, TierId = "ml", Sample = new SampleDraw(1, "uniform", true) }),
            []);
        var error = Assert.Throws<ArgumentException>(() => RecheckRecords.From(decision, shown, Policy));
        Assert.Contains("blind", error.Message);
    }

    [Fact]
    public async Task AnAnchoredTeacher_ShowsAsAgreementGained_AndAccuracyLost()
    {
        // 40 decisions the lower tier answered "OK". Blind, the teacher says HIGH on 12 of them — and
        // HIGH is the known answer on those 12. Shown "OK", it takes it every time.
        var router = Router();
        var pairs = new List<PairRecord>();
        for (var i = 0; i < 40; i++)
        {
            var truth = i < 12 ? "HIGH" : "OK";
            var input = new Case($"d{i}", truth);
            var blind = await router.RecheckAsync(input, input.Id, "teacher");
            var shown = await router.RecheckShownAsync(input, input.Id, "teacher", "OK");
            pairs.Add(AnchoringMeasure.Pair("OK", blind, shown, "s", Policy, new Known<string>(truth)));
        }

        var cell = AnchoringMeasure.Compute(pairs, "teacher");

        Assert.Equal(40, cell.Pairs);
        Assert.Equal(28 / 40.0, cell.AgreementBlind);
        Assert.Equal(1.0, cell.AgreementShown);
        Assert.Equal(12, cell.MovedToLower);
        Assert.Equal(0, cell.MovedAway);
        Assert.True(cell.AgreementShiftP < 0.001);
        Assert.Equal(40, cell.KnownPairs);
        Assert.Equal(1.0, cell.AccuracyBlind);
        Assert.Equal(28 / 40.0, cell.AccuracyShown);
        Assert.True(cell.AccuracyShiftP < 0.001);
    }

    [Fact]
    public async Task APair_IsOneBlindAndOneShownJudgment_OfOneDecision()
    {
        var router = Router();
        var input = new Case("d1", "OK");
        var blind = await router.RecheckAsync(input, "d1", "teacher");
        var shown = await router.RecheckShownAsync(input, "d1", "teacher", "OK");
        var other = await router.RecheckShownAsync(new Case("d2", "OK"), "d2", "teacher", "OK");

        Assert.Throws<ArgumentException>(() => AnchoringMeasure.Pair("OK", shown, blind, "s", Policy));
        Assert.Throws<ArgumentException>(() => AnchoringMeasure.Pair("OK", blind, other, "s", Policy));
        var pair = AnchoringMeasure.Pair("OK", blind, shown, "s", Policy);
        Assert.Null(pair.CorrectBlind);   // no known answer: not scored
    }
}
