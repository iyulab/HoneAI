using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using HoneAI.Routing;
using HoneAI.Trust;

namespace HoneAI.Tests;

/// <summary>
/// The ledger's numbers: agreement weighted by 1/π, its interval with failed rechecks at both ends,
/// and the conditional must-catch miss rate with its exact upper bound — and the stores the
/// rechecks come from, which count a decision once and window it by decision time.
/// </summary>
public sealed class TrustLedgerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "honeai-ledger-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static RecheckRecord Record(string id, double pi, RecheckOutcome outcome, bool mustCatch = false,
        DateTimeOffset? at = null, string tier = "ml", string slice = "s") => new()
        {
            DecisionId = id,
            TierId = tier,
            SliceKey = slice,
            DecidedAt = at ?? T0.AddMinutes(1),
            Probability = pi,
            Outcome = outcome,
            UpperMustCatch = mustCatch,
            Knob = "uniform",
        };

    [Fact]
    public void Agreement_UnderATiltedDraw_IsUnbiased_WhereTheRawShareIsNot()
    {
        // 40,000 settled decisions; the hard tenth disagrees with the tier above 40% of the time,
        // the rest 1%. True agreement 0.951. Hard decisions are drawn at 0.5, easy at 0.02 — the
        // tilt a slice-watch knob produces.
        var records = new List<RecheckRecord>();
        double trueAgreed = 0;
        const int population = 40_000;
        for (var i = 0; i < population; i++)
        {
            var hard = i % 10 == 0;
            var agrees = RecheckDraw.Uniform($"truth-{i}", "v1") >= (hard ? 0.40 : 0.01);
            if (agrees) trueAgreed++;
            var pi = hard ? 0.5 : 0.02;
            if (RecheckDraw.IsDrawn($"d-{i}", "v1", pi))
                records.Add(Record($"d-{i}", pi, agrees ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed));
        }
        var truth = trueAgreed / population;

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1));

        var raw = (double)records.Count(r => r.Outcome == RecheckOutcome.Agreed) / records.Count;
        Assert.InRange(cell.Agreement!.Value, truth - 0.01, truth + 0.01);
        Assert.True(Math.Abs(raw - truth) > 0.05, $"raw {raw} vs truth {truth}: the tilt should bias the raw share");
        Assert.InRange(truth, cell.AgreementLower!.Value, cell.AgreementUpper!.Value);
        Assert.True(cell.EffectiveSampleSize < records.Count, "unequal weights cost effective sample size");
    }

    [Fact]
    public void FailedRechecks_WidenTheInterval_AtBothEnds()
    {
        var answered = Enumerable.Range(0, 90).Select(i => Record($"a{i}", 0.1, i < 85 ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed)).ToList();
        var withFailures = answered.Concat(Enumerable.Range(0, 10).Select(i => Record($"f{i}", 0.1, RecheckOutcome.UpperFailed))).ToList();

        var clean = TrustLedger.Compute(answered, "ml", "s", T0, T0.AddDays(1));
        var failed = TrustLedger.Compute(withFailures, "ml", "s", T0, T0.AddDays(1));

        Assert.Equal(clean.Agreement, failed.Agreement); // the point estimate is over answered rechecks
        Assert.Equal(10, failed.UpperFailures);
        Assert.Equal(0.1, failed.UpperFailureRate, 6);
        Assert.True(failed.AgreementLower < clean.AgreementLower);
        Assert.True(failed.AgreementUpper > clean.AgreementUpper);
    }

    [Fact]
    public void ZeroSeenMisses_StillBoundTheMissRate_ByLnOneOverAlphaOverPi()
    {
        // 500 rechecks at π = 0.01, none a must-catch; 100 must-catch decisions were sent up and caught.
        var records = Enumerable.Range(0, 500).Select(i => Record($"d{i}", 0.01, RecheckOutcome.Agreed)).ToList();

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1), caught: 100);

        Assert.Equal(0, cell.MissesEstimated);
        Assert.Equal(0, cell.MissRate);
        var bound = TrustLedger.BinomialCountUpperBound(0, 0.01, 0.05);
        Assert.InRange(bound, Math.Log(20) / 0.01 - 3, Math.Log(20) / 0.01); // ≈ 299.6, the zero-misses rule
        Assert.Equal(bound / (bound + 100), cell.MissRateUpper!.Value, 9);
        Assert.True(cell.MissRateUpper > 0.7, "100 caught is far too few to claim a low miss rate at π = 0.01");
    }

    [Theory]
    [InlineData(1.0, 0, 0.0, 0.0)]                 // every settled decision rechecked and none missed: a census, nothing unobserved
    [InlineData(1.0, 2, 2.0, 2.0 / 151)]           // two failed rechecks could each hide a miss
    [InlineData(0.1, 0, 28.0, 28.0 / 177)]         // ln(20) / 0.1, the zero-misses rule
    [InlineData(0.1, 2, 60.0, 60.0 / 209)]
    public void MissRateUpper_BoundsTheWindowsCount_SoACensusLeavesNothingToBound(double pi, int failed, double missesUpper, double rateUpper)
    {
        var records = Enumerable.Range(0, 100 - failed).Select(i => Record($"a{i}", pi, RecheckOutcome.Agreed))
            .Concat(Enumerable.Range(0, failed).Select(i => Record($"f{i}", pi, RecheckOutcome.UpperFailed)))
            .ToList();

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1), caught: 149);

        Assert.Equal(missesUpper, cell.MissesUpper!.Value, 9);
        Assert.Equal(rateUpper, cell.MissRateUpper!.Value, 9);
    }

    [Fact]
    public void SeenMisses_AreCountedAtOneOverPi_AndFailuresBoundAsMisses()
    {
        var records = new List<RecheckRecord>
        {
            Record("m1", 0.1, RecheckOutcome.Disagreed, mustCatch: true),
            Record("m2", 0.1, RecheckOutcome.Disagreed, mustCatch: true),
            Record("ok", 0.1, RecheckOutcome.Agreed),
        };

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1), caught: 80);
        var withFailure = TrustLedger.Compute(records.Append(Record("f", 0.1, RecheckOutcome.UpperFailed)), "ml", "s", T0, T0.AddDays(1), caught: 80);

        Assert.Equal(20, cell.MissesEstimated, 9);
        Assert.Equal(0.2, cell.MissRate!.Value, 9);
        Assert.True(cell.MissRateUpper > cell.MissRate);
        Assert.True(withFailure.MissRateUpper > cell.MissRateUpper, "a failed recheck could hide a miss");
        Assert.InRange(cell.MissRateLower!.Value, 0.0001, cell.MissRate!.Value);   // two seen at π = 0.1 show some misses
        Assert.Equal(cell.MissRateLower, withFailure.MissRateLower);               // a failure is no miss for the lower bound
        Assert.Equal(cell.MissRate, withFailure.MissRate);
    }

    [Fact]
    public void TheWindow_IsDecisionTime_AndOtherTiersAndSlicesStayOut()
    {
        var records = new[]
        {
            Record("in", 0.5, RecheckOutcome.Agreed, at: T0.AddHours(1)),
            Record("before", 0.5, RecheckOutcome.Disagreed, at: T0.AddHours(-1)),
            Record("end", 0.5, RecheckOutcome.Disagreed, at: T0.AddDays(1)), // [from, to)
            Record("other-tier", 0.5, RecheckOutcome.Disagreed, tier: "llm"),
            Record("other-slice", 0.5, RecheckOutcome.Disagreed, slice: "t"),
        };

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1));

        Assert.Equal(1, cell.Rechecks);
        Assert.Equal(1.0, cell.Agreement);
    }

    [Fact]
    public void NothingCaught_MakesTheMissRateOne_ButTheHiddenCountStaysBounded()
    {
        // 500 rechecks at π = 0.1, none a must-catch, and the tier above caught no must-catch decision at all: any miss
        // would be every must-catch decision the slice has, so the rate's bound is 1 — whatever the sample. The count is not.
        var records = Enumerable.Range(0, 500).Select(i => Record($"d{i}", 0.1, RecheckOutcome.Agreed)).ToList();

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1), caught: 0);

        Assert.Equal(1.0, cell.MissRateUpper!.Value, 9);
        Assert.Equal(TrustLedger.BinomialCountUpperBound(0, 0.1, 0.05), cell.MissesUpper!.Value, 9);
        Assert.InRange(cell.MissesUpper!.Value, Math.Log(20) / 0.1 - 3, Math.Log(20) / 0.1);   // ≈ 29, the zero-misses rule
    }

    [Fact]
    public void AnEmptyWindow_SaysNothing()
    {
        var cell = TrustLedger.Compute([], "ml", "s", T0, T0.AddDays(1));

        Assert.Equal(0, cell.Rechecks);
        Assert.Null(cell.Agreement);
        Assert.Null(cell.AgreementLower);
        Assert.Null(cell.MissRate);
        Assert.Null(cell.MissRateUpper);
        Assert.Null(cell.MissesUpper);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void AProbabilityOutsideZeroToOne_IsRefused(double pi)
        => Assert.Throws<ArgumentException>(() =>
            TrustLedger.Compute([Record("x", pi, RecheckOutcome.Agreed)], "ml", "s", T0, T0.AddDays(1)));

    private static RecheckRecord Ranked(string id, double pi, bool disagreed, double? confidence)
        => Record(id, pi, disagreed ? RecheckOutcome.Disagreed : RecheckOutcome.Agreed) with { Confidence = confidence };

    [Fact]
    public void ConfidenceRankAuc_EqualsTheWeightedPairwiseCount()
    {
        var rng = new Random(5);
        var records = Enumerable.Range(0, 300).Select(i =>
        {
            var disagreed = rng.NextDouble() < 0.2;
            var confidence = Math.Round(disagreed ? rng.NextDouble() * 0.8 : 0.3 + rng.NextDouble() * 0.7, 1); // rounding makes ties
            return Ranked($"r{i}", rng.NextDouble() < 0.5 ? 0.1 : 0.4, disagreed, confidence);
        }).ToList();

        double favorable = 0, total = 0;
        foreach (var d in records.Where(r => r.Outcome == RecheckOutcome.Disagreed))
            foreach (var a in records.Where(r => r.Outcome == RecheckOutcome.Agreed))
            {
                var w = (1 / d.Probability) * (1 / a.Probability);
                total += w;
                favorable += d.Confidence < a.Confidence ? w : d.Confidence == a.Confidence ? 0.5 * w : 0;
            }

        var cell = TrustLedger.Compute(records, "ml", "s", T0, T0.AddDays(1));

        Assert.Equal(favorable / total, cell.ConfidenceRankAuc!.Value, 9);
        Assert.True(cell.ConfidenceRankAuc > 0.7);
    }

    [Fact]
    public void ConfidenceRankAuc_IsAHalfForConfidenceThatSaysNothing_AndLowWhenItIsBackwards()
    {
        var flat = Enumerable.Range(0, 40).Select(i => Ranked($"f{i}", 0.2, i % 4 == 0, 0.9)).ToList();
        var backwards = Enumerable.Range(0, 40).Select(i => Ranked($"b{i}", 0.2, i % 4 == 0, i % 4 == 0 ? 0.95 : 0.6)).ToList();
        var none = Enumerable.Range(0, 40).Select(i => Ranked($"n{i}", 0.2, i % 4 == 0, null)).ToList();

        Assert.Equal(0.5, TrustLedger.Compute(flat, "ml", "s", T0, T0.AddDays(1)).ConfidenceRankAuc);
        Assert.Equal(0.0, TrustLedger.Compute(backwards, "ml", "s", T0, T0.AddDays(1)).ConfidenceRankAuc);
        Assert.Null(TrustLedger.Compute(none, "ml", "s", T0, T0.AddDays(1)).ConfidenceRankAuc);
    }

    [Fact]
    public void TheRecentWindow_StandsForTheLastDecisions_AndShowsADriftTheCumulativeHides()
    {
        // 200 rechecks at π = 0.1, one an hour: the first 180 agree, the last 20 all disagree.
        var records = Enumerable.Range(0, 200).Select(i =>
            Record($"d{i}", 0.1, i < 180 ? RecheckOutcome.Agreed : RecheckOutcome.Disagreed, at: T0.AddHours(i))).ToList();
        var asOf = T0.AddHours(200);

        var start = TrustLedger.RecentWindowStart(records, "ml", "s", asOf, decisions: 100);
        var recent = TrustLedger.Compute(records, "ml", "s", start!.Value, asOf);
        var cumulative = TrustLedger.Compute(records, "ml", "s", T0, asOf);

        Assert.Equal(T0.AddHours(190), start); // 10 rechecks × 1/0.1 = 100 decisions
        Assert.Equal(10, recent.Rechecks);
        Assert.Equal(0.0, recent.Agreement);
        Assert.Equal(0.9, cumulative.Agreement!.Value, 9);
        Assert.Null(TrustLedger.RecentWindowStart([], "ml", "s", asOf, 100));
    }

    [Fact]
    public void NormalQuantile_MatchesKnownValues()
    {
        Assert.Equal(1.959964, TrustLedger.NormalQuantile(0.975), 5);
        Assert.Equal(-1.644854, TrustLedger.NormalQuantile(0.05), 5);
        Assert.Equal(0, TrustLedger.NormalQuantile(0.5), 9);
    }

    [Fact]
    public async Task Stores_RecordADecisionOnce_AndReadByDecisionTime()
    {
        foreach (IRecheckStore store in new IRecheckStore[] { new InMemoryRecheckStore(), new JsonlRecheckStore(Path.Combine(_dir, "a", "rechecks.jsonl")) })
        {
            Assert.True(await store.AppendAsync(Record("d1", 0.2, RecheckOutcome.Agreed, at: T0.AddHours(2))));
            Assert.False(await store.AppendAsync(Record("d1", 0.2, RecheckOutcome.Disagreed, at: T0.AddHours(2)))); // a redelivery
            Assert.True(await store.AppendAsync(Record("d1", 0.2, RecheckOutcome.Agreed, tier: "llm")));            // another tier's recheck
            Assert.True(await store.AppendAsync(Record("d2", 0.2, RecheckOutcome.Disagreed, at: T0.AddDays(2))));

            var window = new List<RecheckRecord>();
            await foreach (var r in store.ReadAsync("ml", "s", T0, T0.AddDays(1)))
                window.Add(r);

            var only = Assert.Single(window);
            Assert.Equal(("d1", RecheckOutcome.Agreed), (only.DecisionId, only.Outcome));
        }
    }

    [Fact]
    public async Task JsonlStore_ReopenedOnTheSameFile_StillRecordsADecisionOnce()
    {
        var path = Path.Combine(_dir, "b", "rechecks.jsonl");
        Assert.True(await new JsonlRecheckStore(path).AppendAsync(Record("d1", 0.25, RecheckOutcome.Disagreed, mustCatch: true)));

        var reopened = new JsonlRecheckStore(path);
        Assert.False(await reopened.AppendAsync(Record("d1", 0.25, RecheckOutcome.Agreed)));

        var read = new List<RecheckRecord>();
        await foreach (var r in reopened.ReadAsync("ml", "s", T0, T0.AddDays(1)))
            read.Add(r);
        var record = Assert.Single(read);
        Assert.Equal(0.25, record.Probability);
        Assert.True(record.UpperMustCatch);
        Assert.Equal(RecheckOutcome.Disagreed, record.Outcome);
        Assert.Contains("\"outcome\":\"Disagreed\"", await File.ReadAllTextAsync(path)); // readable on disk
    }
}
