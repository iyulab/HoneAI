using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Learning;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class LabelConfirmationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "exp-3", CostRank = 1 };
    private static readonly Tier Llm = new() { Id = "llm", Kind = JudgeKind.Llm, Version = "p2", CostRank = 2 };
    private static readonly Tier Person = new() { Id = "person-a", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
    private static readonly Tier[] Ladder = [Ml, Llm, Person];

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);

    private static ITracedPrediction<string> Answer(Tier tier, string value)
        => new TracedPrediction<string>(value, new PredictionProvenance { Kind = tier.Kind, TierId = tier.Id, TierVersion = tier.Version });

    private static PredictionProvenance SettledBy(Tier tier, SampleDraw? sample = null)
        => new() { Kind = tier.Kind, TierId = tier.Id, SliceKey = "s", SnapshotVersion = "snap-1", Sample = sample };

    private static Confirmation<string> Decide(
        PredictionProvenance decision, IReadOnlyList<ITracedPrediction<string>> answers,
        Func<string, TeacherCell?>? cells = null, Tier? learner = null)
        => LabelConfirmation.Decide("d1", decision, answers, learner ?? Ml, Ladder, Policy, cells ?? (_ => null), Now,
            annotations: new Dictionary<string, string> { [MLoopFeedbackSink<string>.PredictionIdAnnotation] = "p-1" });

    private static TeacherCell Cell(string tier, int plants, double accuracy)
        => new()
        {
            TierId = tier, WindowStart = Now.AddDays(-7), WindowEnd = Now, Plants = plants, Failures = 0,
            MustCatchPlants = 0, MustCatchCaught = 0, MustCatchFailures = 0, Accuracy = accuracy, Confidence = 0.95,
        };

    [Fact]
    public void ADecisionThatWentUp_IsALabel_ReachedWithCertainty()
    {
        // The ML tier escalated; the LLM settled. Every escalated decision is judged above: π = 1.
        var result = Decide(SettledBy(Llm), [Answer(Llm, "minor")]);

        Assert.Equal(ConfirmationOutcome.Confirmed, result.Outcome);
        var label = result.Label!;
        Assert.Equal("minor", label.Value);
        Assert.Equal("llm", label.ConfirmedBy);
        Assert.Equal("p2", label.ConfirmedByVersion);
        Assert.Equal(1.0, label.Probability);
        Assert.Equal(0.8, label.Trust);                         // LLM's default — the ledger has not measured it
        Assert.Equal(LabelTrustBasis.Default, label.TrustBasis);
        Assert.Equal(0.8, label.Weight, 12);
        Assert.Equal("snap-1", label.SnapshotVersion);
        Assert.Equal("p-1", label.Annotations![MLoopFeedbackSink<string>.PredictionIdAnnotation]);
    }

    [Fact]
    public void ARecheckOfASettledDecision_IsWeightedByItsDraw()
    {
        // ML settled, and the decision was drawn with π = 0.25: it stands for four such decisions.
        var result = Decide(SettledBy(Ml, new SampleDraw(0.25, "promise", Drawn: true)), [Answer(Person, "ok")]);

        var label = Assert.IsType<ConfirmedLabel<string>>(result.Label);
        Assert.Equal(0.25, label.Probability);
        Assert.Equal(1.0, label.Trust);
        Assert.Equal(4.0, label.Weight, 12);
    }

    [Fact]
    public void ASettledDecisionNotDrawn_IsNotALabel()
    {
        var result = Decide(SettledBy(Ml, new SampleDraw(0.25, "promise", Drawn: false)), [Answer(Llm, "ok")]);

        Assert.Equal(ConfirmationOutcome.NotALabel, result.Outcome);
        Assert.Null(result.Label);
    }

    [Fact]
    public void ATierConfirmingItself_IsNotALabel()
    {
        // The ML tier's own answer is not evidence about the ML tier.
        var result = Decide(SettledBy(Ml, new SampleDraw(1.0, "shadow", Drawn: true)), [Answer(Ml, "ok")]);

        Assert.Equal(ConfirmationOutcome.NotALabel, result.Outcome);
    }

    [Fact]
    public void JudgesAboveDisagreeingOnAMustCatchValue_WaitForConsensus()
    {
        // A person says danger, the LLM says ok: neither wins automatically.
        var result = Decide(SettledBy(Llm), [Answer(Llm, "ok"), Answer(Person, "danger")]);

        Assert.Equal(ConfirmationOutcome.NeedsConsensus, result.Outcome);
        Assert.Null(result.Label);
    }

    [Fact]
    public void JudgesAboveDisagreeingOnOrdinaryValues_TheMostTrustedStands()
    {
        var result = Decide(SettledBy(Llm), [Answer(Llm, "ok"), Answer(Person, "minor")]);

        Assert.Equal("minor", result.Label!.Value);
        Assert.Equal("person-a", result.Label.ConfirmedBy);
    }

    [Fact]
    public void MeasuredTrust_ReplacesTheKindsDefault()
    {
        // The ledger measured the LLM at 0.6 on planted items — below the person's default and its own.
        var result = Decide(SettledBy(Llm), [Answer(Llm, "minor")], cells: id => id == "llm" ? Cell("llm", 40, 0.6) : null);

        Assert.Equal(0.6, result.Label!.Trust);
        Assert.Equal(LabelTrustBasis.Measured, result.Label.TrustBasis);
    }

    [Fact]
    public void ACellWithoutPlants_IsNotAMeasurement()
    {
        var result = Decide(SettledBy(Llm), [Answer(Llm, "minor")], cells: _ => Cell("llm", 0, 0.1));

        Assert.Equal(LabelTrustBasis.Default, result.Label!.TrustBasis);
    }

    [Fact]
    public async Task TheMLoopSink_RecordsWeightedFeedback_OnTheMLTiersPrediction()
    {
        var label = Decide(SettledBy(Ml, new SampleDraw(0.5, "promise", Drawn: true)), [Answer(Llm, "minor")]).Label!;
        var client = new FeedbackRecorder();

        await new MLoopFeedbackSink<string>(client).SendAsync(label);

        var sent = Assert.Single(client.Sent);
        Assert.Equal("p-1", sent.PredictionId);
        Assert.Equal("minor", sent.ActualValue);
        Assert.Equal(1.6, sent.Weight!.Value, 12);              // trust 0.8 / π 0.5
        Assert.Equal("llm@p2 (default trust)", sent.Source);
    }

    [Fact]
    public async Task TheMLoopSink_RefusesALabelWithoutAPredictionId()
    {
        var label = Decide(SettledBy(Llm), [Answer(Llm, "minor")]).Label! with { Annotations = null };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new MLoopFeedbackSink<string>(new FeedbackRecorder()).SendAsync(label));
        Assert.Contains(MLoopFeedbackSink<string>.PredictionIdAnnotation, error.Message);
    }

    [Fact]
    public void ReferenceTo_CarriesTheLoggedPredictionId_AndRefusesAnUnloggedOne()
    {
        var reference = MLoopFeedbackSink<string>.ReferenceTo(new MLoopPredictionResult(new Dictionary<string, object?>(), "p-9"));
        Assert.Equal("p-9", reference[MLoopFeedbackSink<string>.PredictionIdAnnotation]);

        Assert.Throws<ArgumentException>(() =>
            MLoopFeedbackSink<string>.ReferenceTo(new MLoopPredictionResult(new Dictionary<string, object?>())));
    }

    private sealed class FeedbackRecorder : IMLoopClient
    {
        public List<MLoopFeedback> Sent { get; } = new();

        public Task RecordFeedbackAsync(MLoopFeedback feedback, CancellationToken ct = default)
        {
            Sent.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(MLoopPredictionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(MLoopForecastRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
