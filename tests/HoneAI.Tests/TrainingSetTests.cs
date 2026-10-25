using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HoneAI;
using HoneAI.Learning;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class TrainingSetTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly Tier Ml = new() { Id = "ml", Kind = JudgeKind.Ml, Version = "exp-3", CostRank = 1 };
    private static readonly Tier Llm = new() { Id = "llm", Kind = JudgeKind.Llm, Version = "p2", CostRank = 2 };
    private static readonly Tier Person = new() { Id = "person-a", Kind = JudgeKind.Human, Version = "1", CostRank = 3 };
    private static readonly Tier[] Ladder = [Ml, Llm, Person];

    private static readonly ISettlementPolicy<string> Policy =
        SettlementPolicy.Categorical(settleable: ["ok", "minor"], mustCatch: ["danger"]);

    private static ITracedPrediction<string> Answer(Tier tier, string value)
        => new TracedPrediction<string>(value, new PredictionProvenance { Kind = tier.Kind, TierId = tier.Id, TierVersion = tier.Version });

    // Sent all the way up: every tier above the learner saw it (π = 1).
    private static readonly PredictionProvenance WentUp = new() { Kind = JudgeKind.Human, TierId = "person-a", SliceKey = "s" };

    private static ConfirmedLabel<string> Label(string id, string value, DateTimeOffset at, Tier? by = null)
        => LabelConfirmation.Decide(id, WentUp, [Answer(by ?? Llm, value)], Ml, Ladder, Policy, _ => null, at).Label!;

    [Fact]
    public void AManifest_CountsTheLabels_AndItsHashDoesNotDependOnTheOrderTheyWereKept()
    {
        var labels = new[]
        {
            Label("d1", "ok", Now),
            Label("d2", "minor", Now.AddHours(1)),
            Label("d3", "ok", Now.AddHours(2), Person),
        };

        var manifest = TrainingSets.Manifest(labels);

        Assert.Equal(3, manifest.Labels);
        Assert.Equal(0.8 + 0.8 + 1.0, manifest.TotalWeight, 12);
        Assert.Equal(new Dictionary<string, int> { ["llm@p2"] = 2, ["person-a@1"] = 1 }, manifest.BySource);
        Assert.Equal(new Dictionary<string, int> { ["Default"] = 3 }, manifest.ByTrustBasis);
        Assert.Equal(new Dictionary<string, int> { ["s"] = 3 }, manifest.BySlice);
        Assert.Equal(new Dictionary<string, int> { ["minor"] = 1, ["ok"] = 2 }, manifest.ByValue);
        Assert.Equal(Now, manifest.From);
        Assert.Equal(Now.AddHours(2), manifest.To);
        Assert.Null(manifest.EvaluationShare);

        Assert.Equal(manifest.ContentSha256, TrainingSets.Manifest(labels.Reverse()).ContentSha256);
        Assert.NotEqual(manifest.ContentSha256, TrainingSets.Manifest(labels.Take(2)).ContentSha256);
        Assert.NotEqual(manifest.ContentSha256,
            TrainingSets.Manifest([labels[0], labels[1], labels[2] with { Value = "minor" }]).ContentSha256);
    }

    [Fact]
    public void AManifest_RefusesAHeldOutLabel_AndTwoLabelsForOneDecision_AndRecordsTheSplit()
    {
        var label = Label("d1", "ok", Now);
        Assert.Throws<ArgumentException>(() => TrainingSets.Manifest([label with { Share = LabelShare.Evaluation, ShareProbability = 0.2 }]));
        Assert.Throws<ArgumentException>(() => TrainingSets.Manifest([label, label with { Value = "minor" }]));

        var split = new LabelSplit { EvaluationShare = 0.2, Salt = "v1" };
        var manifest = TrainingSets.Manifest([label], split);
        Assert.Equal(0.2, manifest.EvaluationShare);
        Assert.Equal("v1", manifest.SplitSalt);
        Assert.Null(TrainingSets.Manifest([label], LabelSplit.None).EvaluationShare);
    }

    [Fact]
    public void Compare_ListsWhatChanged_AndWhetherTheSplitDid()
    {
        var earlier = TrainingSets.Manifest([Label("d1", "ok", Now), Label("d2", "minor", Now)]);
        var later = TrainingSets.Manifest([Label("d1", "ok", Now), Label("d3", "danger", Now, Person)], new LabelSplit { EvaluationShare = 0.1 });

        var change = TrainingSets.Compare(earlier, later);

        Assert.Equal(0, change.Labels);
        Assert.Equal(new Dictionary<string, int> { ["llm@p2"] = -1, ["person-a@1"] = 1 }, change.BySource);
        Assert.Equal(new Dictionary<string, int> { ["danger"] = 1, ["minor"] = -1 }, change.ByValue);
        Assert.Empty(change.BySlice);
        Assert.True(change.SplitChanged);
        Assert.False(TrainingSets.Compare(earlier, earlier).SplitChanged);
    }

    [Fact]
    public async Task ARunKeepsItsTrainingSetManifest_AcrossTheStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "honeai-runs-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var manifest = TrainingSets.Manifest([Label("d1", "ok", Now)], new LabelSplit { EvaluationShare = 0.2 });
            var run = new LifecycleRun("run-1", "running", [])
            {
                Request = new LifecycleRequest(new MLoopTrainRequest("data.csv", "label"), TrainingSet: manifest),
            };

            await new JsonlLifecycleStore(path).SaveAsync(run);
            var read = (await new JsonlLifecycleStore(path).GetAsync("run-1"))!.Request!.TrainingSet!;

            Assert.Equal(manifest.ContentSha256, read.ContentSha256);
            Assert.Equal(manifest.BySource, read.BySource);
            Assert.Equal(0.2, read.EvaluationShare);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Admission_KeepsOutAnUnqualifiedJudge_SoTheNextAdmittedOneStands()
    {
        // The person is trusted more, but not qualified: the LLM's answer stands.
        var admission = new LabelAdmission { Qualified = j => j.TierId != "person-a" };
        var result = LabelConfirmation.Decide("d1", WentUp, [Answer(Llm, "minor"), Answer(Person, "ok")], Ml, Ladder, Policy,
            _ => null, Now, admission: admission);
        Assert.Equal("minor", result.Label!.Value);

        var none = LabelConfirmation.Decide("d1", WentUp, [Answer(Person, "ok")], Ml, Ladder, Policy, _ => null, Now, admission: admission);
        Assert.Equal(ConfirmationOutcome.NotAdmitted, none.Outcome);
        Assert.Contains("not qualified", none.Reason);
    }

    [Fact]
    public void Admission_CapsAnUnmeasuredJudgesTrust_AndHoldsLabelsToAMinimum()
    {
        var measured = new TeacherCell
        {
            TierId = "llm",
            WindowStart = Now.AddDays(-7),
            WindowEnd = Now,
            Plants = 40,
            Failures = 0,
            MustCatchPlants = 0,
            MustCatchCaught = 0,
            MustCatchFailures = 0,
            Accuracy = 0.7,
            Confidence = 0.95,
        };
        Func<JudgeIdentity, TeacherCell?> cells = j => j.TierId == "llm" ? measured : null;

        // Unmeasured, the person's default 1.0 is capped at 0.6 — under the LLM's measured 0.7, which now stands.
        var capped = LabelConfirmation.Decide("d1", WentUp, [Answer(Llm, "minor"), Answer(Person, "ok")], Ml, Ladder, Policy,
            cells, Now, admission: new LabelAdmission { DefaultTrustCap = 0.6 });
        Assert.Equal("minor", capped.Label!.Value);
        Assert.Equal(LabelTrustBasis.Measured, capped.Label.TrustBasis);

        var person = LabelConfirmation.Decide("d1", WentUp, [Answer(Person, "ok")], Ml, Ladder, Policy, _ => null, Now,
            admission: new LabelAdmission { DefaultTrustCap = 0.6 });
        Assert.Equal(0.6, person.Label!.Trust);

        var refused = LabelConfirmation.Decide("d1", WentUp, [Answer(Llm, "minor")], Ml, Ladder, Policy, _ => null, Now,
            admission: new LabelAdmission { MinimumTrust = 0.9 });
        Assert.Equal(ConfirmationOutcome.NotAdmitted, refused.Outcome);

        var consensus = new ConsensusJudgment<string>
        {
            DecisionId = "d1",
            Value = "minor",
            ReachedAt = Now,
            Participants = [new JudgeIdentity("llm"), new JudgeIdentity("person-a")],
        };
        Assert.Equal(ConfirmationOutcome.NotAdmitted, LabelConfirmation.Settle(WentUp, consensus, Ml, Ladder,
            defaults: LabelTrustDefaults.Standard with { Consensus = 0.8 }, admission: new LabelAdmission { MinimumTrust = 0.9 }).Outcome);
    }
}
