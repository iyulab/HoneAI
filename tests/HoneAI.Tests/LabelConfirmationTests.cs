using System;
using System.Collections.Generic;
using System.Linq;
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
        Func<JudgeIdentity, TeacherCell?>? cells = null, Tier? learner = null)
        => LabelConfirmation.Decide("d1", decision, answers, learner ?? Ml, Ladder, Policy, cells ?? (_ => null), Now,
            annotations: new Dictionary<string, string> { [MLoopFeedbackSink<string>.PredictionIdAnnotation] = "p-1" });

    private static TeacherCell Cell(string tier, int plants, double accuracy)
        => new()
        {
            TierId = tier,
            WindowStart = Now.AddDays(-7),
            WindowEnd = Now,
            Plants = plants,
            Failures = 0,
            MustCatchPlants = 0,
            MustCatchCaught = 0,
            MustCatchFailures = 0,
            Accuracy = accuracy,
            Confidence = 0.95,
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

    private static ConsensusJudgment<string> Consensus(string value, params string[] participants)
        => new()
        {
            DecisionId = "d1",
            Value = value,
            ReachedAt = Now,
            Grounds = "case-7",
            Participants = participants.Select(p => p.Split('/') is [var tier, var who] ? new JudgeIdentity(tier, who) : new JudgeIdentity(p)).ToList(),
        };

    private static Confirmation<string> Settle(PredictionProvenance decision, ConsensusJudgment<string> consensus,
        LabelTrustDefaults? defaults = null)
        => LabelConfirmation.Settle(decision, consensus, Ml, Ladder, defaults,
            annotations: new Dictionary<string, string> { [MLoopFeedbackSink<string>.PredictionIdAnnotation] = "p-1" });

    [Fact]
    public void AConsensus_SettlesTheDecisionThatWaited_AsItsOwnValue()
    {
        // The LLM said ok, the person danger; together they settled on minor — neither original answer won.
        Assert.Equal(ConfirmationOutcome.NeedsConsensus,
            Decide(SettledBy(Llm), [Answer(Llm, "ok"), Answer(Person, "danger")]).Outcome);

        var result = Settle(SettledBy(Llm), Consensus("minor", "person-a", "llm"));

        Assert.Equal(ConfirmationOutcome.Confirmed, result.Outcome);
        var label = result.Label!;
        Assert.Equal("minor", label.Value);
        Assert.Equal("llm+person-a", label.ConfirmedBy);                 // the cost order, not the order given
        Assert.Equal([new JudgeIdentity("llm"), new JudgeIdentity("person-a")], label.Participants);
        Assert.Equal(JudgeKind.Human, label.Kind);
        Assert.Equal(LabelTrustBasis.Consensus, label.TrustBasis);
        Assert.Equal(1.0, label.Trust);
        Assert.Equal(1.0, label.Probability);
        Assert.Equal("snap-1", label.SnapshotVersion);
        Assert.Equal(Now, label.ConfirmedAt);
    }

    [Fact]
    public void AConsensusOnASettledDecision_IsWeightedByItsDraw_AndItsTrustIsTheConsumers()
    {
        var result = Settle(SettledBy(Ml, new SampleDraw(0.5, "promise", Drawn: true)), Consensus("danger", "llm", "person-a"),
            new LabelTrustDefaults { Consensus = 0.9 });

        Assert.Equal(0.5, result.Label!.Probability);
        Assert.Equal(1.8, result.Label.Weight, 12);
    }

    [Fact]
    public void AConsensusOnASettledDecisionNeverDrawn_IsNotALabel()
    {
        var result = Settle(SettledBy(Ml, new SampleDraw(0.5, "promise", Drawn: false)), Consensus("ok", "llm", "person-a"));

        Assert.Equal(ConfirmationOutcome.NotALabel, result.Outcome);
    }

    [Theory]
    [InlineData("person-a", null)]          // one judge is not a consensus
    [InlineData("person-a", "person-a")]    // nor is one judge named twice
    [InlineData("person-a", "ml")]          // the learner cannot confirm its own label
    [InlineData("person-a", "stranger")]    // a participant off the ladder
    public void WhatIsNotAConsensus_IsRefused(string first, string? second)
        => Assert.Throws<ArgumentException>(
            () => Settle(SettledBy(Llm), Consensus("ok", second is null ? [first] : [first, second])));

    private static readonly Tier People = new() { Id = "people", Kind = JudgeKind.Human, Version = "1", CostRank = 4 };
    private static readonly Tier[] LadderWithPeople = [Ml, Llm, Person, People];

    private static ITracedPrediction<string> PersonAnswer(string who, string value)
        => new TracedPrediction<string>(value, new PredictionProvenance
        {
            Kind = JudgeKind.Human,
            TierId = People.Id,
            TierVersion = People.Version,
            JudgedBy = who,
        });

    [Fact]
    public void OnARungOfPeople_EachPersonIsTrustedByTheirOwnPlants()
    {
        // Two people share one rung; the ledger measured p-1 at 0.95 and p-2 at 0.7. They disagree on an ordinary
        // value: p-1's answer stands, weighted by p-1's own accuracy — not by the rung's.
        var cells = (JudgeIdentity id) => id switch
        {
            { TierId: "people", JudgedBy: "p-1" } => Cell("people", 30, 0.95),
            { TierId: "people", JudgedBy: "p-2" } => Cell("people", 30, 0.7),
            _ => null,
        };
        var result = LabelConfirmation.Decide("d1", SettledBy(People), [PersonAnswer("p-2", "ok"), PersonAnswer("p-1", "minor")],
            Ml, LadderWithPeople, Policy, cells, Now);

        var label = result.Label!;
        Assert.Equal("minor", label.Value);
        Assert.Equal("people", label.ConfirmedBy);
        Assert.Equal("p-1", label.JudgedBy);
        Assert.Equal(0.95, label.Trust);
        Assert.Equal(LabelTrustBasis.Measured, label.TrustBasis);
        Assert.Equal("people/p-1@1", MLoopFeedbackSink<string>.SourceOf(label));
    }

    [Fact]
    public void TwoPeopleOnOneRung_AreAConsensus_OnePersonTwiceIsNot()
    {
        var label = LabelConfirmation.Settle(SettledBy(People), Consensus("danger", "people/p-2", "people/p-1"), Ml, LadderWithPeople).Label!;

        Assert.Equal("people/p-1+people/p-2", label.ConfirmedBy);
        Assert.Equal(JudgeKind.Human, label.Kind);
        Assert.Throws<ArgumentException>(
            () => LabelConfirmation.Settle(SettledBy(People), Consensus("danger", "people/p-1", "people/p-1"), Ml, LadderWithPeople));
    }

    [Fact]
    public async Task TheMLoopSink_NamesAConsensusByItsParticipants()
    {
        var label = Settle(SettledBy(Llm), Consensus("minor", "llm", "person-a")).Label!;
        var client = new FeedbackRecorder();

        await new MLoopFeedbackSink<string>(client).SendAsync(label);

        Assert.Equal("llm+person-a (consensus)", Assert.Single(client.Sent).Source);
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
        var result = Decide(SettledBy(Llm), [Answer(Llm, "minor")], cells: id => id.TierId == "llm" ? Cell("llm", 40, 0.6) : null);

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
    public void ASplit_HoldsOutAShareOfDecisions_FixedByTheDecision_WithItsProbabilityInTheWeight()
    {
        var split = new LabelSplit { EvaluationShare = 0.2 };
        var labels = Enumerable.Range(0, 2000)
            .Select(i => LabelConfirmation.Decide($"d{i}", SettledBy(Llm), [Answer(Llm, "minor")], Ml, Ladder, Policy,
                _ => null, Now, split: split).Label!)
            .ToList();

        var heldOut = labels.Count(l => l.Share == LabelShare.Evaluation);
        Assert.InRange(heldOut, 340, 460);                                   // ~ 400 of 2000
        Assert.All(labels.Where(l => l.Share == LabelShare.Evaluation), l => Assert.Equal(0.2, l.ShareProbability));
        Assert.All(labels.Where(l => l.Share == LabelShare.Training), l => Assert.Equal(0.8, l.ShareProbability, 12));
        // Each share's weights stand for every decision: they sum to about the number of decisions × trust.
        Assert.InRange(labels.Where(l => l.Share == LabelShare.Evaluation).Sum(l => l.Weight), 0.8 * 2000 * 0.85, 0.8 * 2000 * 1.15);

        // The share belongs to the decision: a later label — a consensus here — falls in the same one.
        foreach (var label in labels.Take(50))
        {
            var consensus = LabelConfirmation.Settle(SettledBy(Llm), Consensus("minor", "person-a", "llm") with { DecisionId = label.DecisionId },
                Ml, Ladder, split: split).Label!;
            Assert.Equal(label.Share, consensus.Share);
        }
    }

    [Fact]
    public void ASplit_PerSlice_AndNeverASliceHeldOutWhole()
    {
        var split = new LabelSplit { EvaluationShare = 0, BySlice = new Dictionary<string, double> { ["rare"] = 0.5 } };
        Assert.Equal((LabelShare.Training, 1.0), split.Assign("d1", "s"));
        Assert.Equal(0.5, split.Assign("d1", "rare").Probability);

        Assert.Throws<ArgumentOutOfRangeException>(() => new LabelSplit { EvaluationShare = 1 }.Assign("d1", null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LabelSplit { EvaluationShare = -0.1 }.Assign("d1", null));

        // No split asked for: every label is for learning, weighted as before.
        var label = Decide(SettledBy(Llm), [Answer(Llm, "minor")]).Label!;
        Assert.Equal(LabelShare.Training, label.Share);
        Assert.Equal(0.8, label.Weight, 12);
    }

    [Fact]
    public async Task TheMLoopSink_RefusesAHeldOutLabel_AndTheShareSinkRoutesIt()
    {
        var label = Decide(SettledBy(Llm), [Answer(Llm, "minor")]).Label! with { Share = LabelShare.Evaluation, ShareProbability = 0.2 };
        var client = new FeedbackRecorder();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new MLoopFeedbackSink<string>(client).SendAsync(label));
        Assert.Empty(client.Sent);

        var evaluation = new LabelRecorder();
        var routed = new LabelShareSink<string>(new MLoopFeedbackSink<string>(client), evaluation);
        await routed.SendAsync(label);
        await routed.SendAsync(label with { Share = LabelShare.Training, ShareProbability = 0.8 });
        Assert.Single(evaluation.Sent);
        Assert.Single(client.Sent);
    }

    [Fact]
    public void Supersedes_LetsAMoreTrustedLabelReplaceAnEarlierOne_NeverALessTrustedOne()
    {
        var llm = Decide(SettledBy(Llm), [Answer(Llm, "minor")]).Label!;                     // trust 0.8, default
        var person = llm with { Value = "ok", ConfirmedBy = "person-a", Kind = JudgeKind.Human, Trust = 1.0, ConfirmedAt = Now.AddHours(1) };
        var consensus = llm with { Value = "danger", Trust = 0.9, TrustBasis = LabelTrustBasis.Consensus, ConfirmedAt = Now.AddHours(2) };
        var measured = llm with { TrustBasis = LabelTrustBasis.Measured };

        Assert.True(LabelConfirmation.Supersedes(llm, person));
        Assert.False(LabelConfirmation.Supersedes(person, llm with { ConfirmedAt = Now.AddHours(3) }));   // later, but trusted less
        Assert.True(LabelConfirmation.Supersedes(person, consensus));                                     // a consensus over any one judge
        Assert.False(LabelConfirmation.Supersedes(consensus, person with { ConfirmedAt = Now.AddHours(3) }));
        Assert.True(LabelConfirmation.Supersedes(llm, measured));                                         // equal trust: measured over default
        Assert.True(LabelConfirmation.Supersedes(llm, llm with { ConfirmedAt = Now.AddMinutes(1) }));    // then the later one

        Assert.Throws<ArgumentException>(() => LabelConfirmation.Supersedes(llm, person with { DecisionId = "d2" }));
    }

    private sealed class LabelRecorder : IConfirmedLabelSink<string>
    {
        public List<ConfirmedLabel<string>> Sent { get; } = new();

        public Task SendAsync(ConfirmedLabel<string> label, CancellationToken cancellationToken = default)
        {
            Sent.Add(label);
            return Task.CompletedTask;
        }
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
        public Task<IReadOnlyList<ITracedPrediction<MLoopPredictionResult>>> PredictImagesAsync(MLoopImagePredictionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedAsync(MLoopEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopEmbedding> EmbedTextsAsync(MLoopTextEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteAsync(string model, string experimentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
