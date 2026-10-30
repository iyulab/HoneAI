using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HoneAI.Routing;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class ExpectedLossAbstainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyList<RiskBin> Calibration = [new RiskBin(0.5, 0.1), new RiskBin(1.0, 0.6)];

    private static KnobSelection Selection(IReadOnlyList<RiskBin>? calibration = null) => new()
    {
        Shape = SelectionShape.WeightedRisk,
        Calibration = calibration ?? Calibration,
        MeanScore = 0.3,
        UniformMix = 0.2,
    };

    [Fact]
    public void TheExpectedLoss_IsTheWeightTimesTheCalibratedProbabilityOfBeingWrong()
    {
        Assert.Equal(1.8, ExpectedLoss.Of(Calibration, new SelectionSignal { Risk = 0.9, Weight = 3 })!.Value, 12);
        Assert.Equal(0.3, ExpectedLoss.Of(Calibration, new SelectionSignal { Risk = 0.2, Weight = 3 })!.Value, 12);
        Assert.Equal(0.0, ExpectedLoss.Of(Calibration, new SelectionSignal { Risk = 0.9, Weight = -2 })!.Value);
    }

    [Fact]
    public void WithoutARisk_OrACalibration_ThereIsNoExpectedLoss()
    {
        Assert.Null(ExpectedLoss.Of(Calibration, new SelectionSignal { Weight = 3 }));
        Assert.Null(ExpectedLoss.Of(Calibration, null));
        Assert.Null(ExpectedLoss.Of([], new SelectionSignal { Risk = 0.9 }));
        var entry = new PolicyEntry { TierId = "ml", SliceKey = "s", State = TrustState.Active };
        Assert.Null(ExpectedLoss.Of(entry, new SelectionSignal { Risk = 0.9 }));
        Assert.Null(ExpectedLoss.Of(entry with { Selection = new Dictionary<string, KnobSelection> { ["u"] = Selection([]) } }, new SelectionSignal { Risk = 0.9 }));
    }

    [Fact]
    public void TheEntrysCalibration_IsTheFirstKnobsThatHasOne()
    {
        var entry = new PolicyEntry
        {
            TierId = "ml",
            SliceKey = "s",
            State = TrustState.Active,
            Selection = new Dictionary<string, KnobSelection> { ["b-empty"] = Selection([]), ["c"] = Selection(), ["a-empty"] = Selection([]) },
        };

        Assert.Equal(1.8, ExpectedLoss.Of(entry, new SelectionSignal { Risk = 0.9, Weight = 3 })!.Value, 12);
    }

    [Theory]
    [InlineData(0.25, 10.0)]   // 10, 11 and 12 — three of the twelve — reach 10: the smallest loss a quarter or more reach
    [InlineData(0.5, 7.0)]
    [InlineData(0.01, 12.0)]
    public void TheLine_HandsUpAtLeastTheShareAsked(double share, double expected)
    {
        double[] losses = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        var line = ExpectedLoss.Line(losses, share)!.Value;

        Assert.Equal(expected, line, 12);
        Assert.True(losses.Count(l => l >= line) >= share * losses.Length);
    }

    [Fact]
    public void TheLine_NeedsLossesAndAShareBetweenZeroAndOne()
    {
        Assert.Null(ExpectedLoss.Line([], 0.1));
        Assert.Null(ExpectedLoss.Line([1.0, 2.0], 0.0));
        Assert.Null(ExpectedLoss.Line([1.0, 2.0], 1.0));
        Assert.Null(ExpectedLoss.Line([double.NaN, double.PositiveInfinity], 0.5));
    }

    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "1", CostRank = 1 };
    private static readonly Tier Top = new() { Id = "top", Kind = JudgeKind.Human, Version = "1", CostRank = 2 };

    private static PolicySnapshot Snapshot(double? line) => new()
    {
        Version = "v",
        IssuedAt = Now,
        SoftExpiresAt = Now.AddHours(1),
        HardExpiresAt = Now.AddHours(2),
        Entries =
        [
            new PolicyEntry
            {
                TierId = "ml", SliceKey = "s", State = TrustState.Active,
                SampleRates = new Dictionary<string, double> { ["uniform"] = 1.0 },
                Selection = new Dictionary<string, KnobSelection> { ["slice-watch"] = Selection() },
                AbstainExpectedLossAbove = line,
            },
        ],
    };

    private sealed class ByInput : ISelectionSignals<string, string>
    {
        public int Asked { get; private set; }

        public SelectionSignal? SignalOf(string input, SelectionContext<string> context)
        {
            Asked++;
            return input switch
            {
                "costly" => new SelectionSignal { Risk = 0.9, Weight = 3 },     // 1.8
                "cheap" => new SelectionSignal { Risk = 0.2, Weight = 3 },      // 0.3
                "doubled" => new SelectionSignal { Risk = 0.9, Weight = 2 },    // 1.2, exactly (a doubling is exact)
                _ => null,
            };
        }
    }

    private static TieredRouter<string, string> Router(double? line, ByInput signals)
        => new([Judge.Constant<string, string>(Ml, "OK", 0.9), Judge.Constant<string, string>(Top, "OK")],
            SettlementPolicy.Categorical(new[] { "OK" }, Array.Empty<string>()),
            SliceKey.From<string>("k", _ => "s"), id => id, () => Snapshot(line),
            new TieredRouterOptions { Clock = () => Now }, signals);

    [Fact]
    public async Task ADecisionAtOrAboveTheLine_GoesUp_AndOneBelowItSettles()
    {
        var signals = new ByInput();
        var router = Router(line: 1.0, signals);

        var costly = await router.DecideAsync("costly");
        var cheap = await router.DecideAsync("cheap");

        Assert.Equal("top", costly.Prediction.Provenance.TierId);
        Assert.Contains(costly.Steps, s => s.Tier.Id == "ml" && s.Outcome == StepOutcome.Escalated && s.Detail == "expected loss at or above the tier's line");
        Assert.Equal("ml", cheap.Prediction.Provenance.TierId);
    }

    [Fact]
    public async Task ADecisionExactlyOnTheLine_GoesUp()
    {
        var router = Router(line: 1.2, new ByInput());

        var decision = await router.DecideAsync("doubled");

        Assert.Equal("top", decision.Prediction.Provenance.TierId);
    }

    [Fact]
    public async Task WithoutALine_OrWithoutARisk_TheDecisionSettlesAsBefore()
    {
        var off = await Router(line: null, new ByInput()).DecideAsync("costly");
        var noSignal = await Router(line: 0.0, new ByInput()).DecideAsync("unknown");

        Assert.Equal("ml", off.Prediction.Provenance.TierId);
        Assert.Equal("ml", noSignal.Prediction.Provenance.TierId);
    }

    [Fact]
    public async Task TheSignal_IsAskedOnceForADecisionThatSettles()
    {
        var signals = new ByInput();

        var decision = await Router(line: 1.0, signals).DecideAsync("cheap");

        Assert.Equal(1, signals.Asked);
        Assert.Equal(0.2, decision.Prediction.Provenance.Sample!.Signal!.Risk);
    }

    [Fact]
    public void TheSnapshotsJson_CarriesTheLine_AndTheHashMovesWithIt()
    {
        var with = Snapshot(1.25);
        var without = Snapshot(null);

        var json = System.Text.Encoding.UTF8.GetString(PolicySnapshotJson.Serialize(with));
        var back = PolicySnapshotJson.Deserialize(PolicySnapshotJson.Serialize(with));

        Assert.Contains("\"abstainExpectedLossAbove\":1.25", json);
        Assert.DoesNotContain("abstainExpectedLossAbove", System.Text.Encoding.UTF8.GetString(PolicySnapshotJson.Serialize(without)));
        Assert.Equal(1.25, back.Entries[0].AbstainExpectedLossAbove);
        Assert.NotEqual(PolicySnapshotJson.ContentHash(with), PolicySnapshotJson.ContentHash(without));
        Assert.Equal(PolicySnapshotJson.ContentHash(with), PolicySnapshotJson.ContentHash(back));
    }

    [Fact]
    public void TheIssuer_CarriesTheLine_ExceptForAShadowTier()
    {
        var policy = new IssuePolicy { SoftLife = TimeSpan.FromDays(1), HardLife = TimeSpan.FromDays(3), ActiveRate = 0.3 };
        SliceStanding In(TrustState state) => new(state, Now.AddDays(-10), "v1");

        var snapshot = PolicySnapshotIssuer.Issue("s-8", Now,
        [
            new SliceIssue("ml", "shadow", In(TrustState.Shadow)) { AbstainExpectedLossAbove = 1.0 },
            new SliceIssue("ml", "active", In(TrustState.Active)) { AbstainExpectedLossAbove = 1.0 },
        ], policy);

        var entry = snapshot.Entries.ToDictionary(e => e.SliceKey);
        Assert.Null(entry["shadow"].AbstainExpectedLossAbove);
        Assert.Equal(1.0, entry["active"].AbstainExpectedLossAbove);
    }
}
