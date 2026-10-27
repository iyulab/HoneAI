using System;
using System.Collections.Generic;
using System.Linq;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class ProbeSelectorTests
{
    private static readonly JudgeIdentity Llm = new("llm");
    private static readonly JudgeIdentity Person = new("people", "p-1");

    private static ProbeCandidate Candidate(int i, double value, ProbeMethod method = ProbeMethod.Recheck, string pool = "tenant-a", JudgeIdentity? target = null)
        => new() { DecisionId = $"d{i}", SliceKey = "s", Target = target ?? Llm, Method = method, Pool = pool, Value = value };

    private static ProbePolicy Policy(double budget, IReadOnlyDictionary<ProbeMethod, double>? shares = null, double floor = 0.01)
        => new() { PoolBudgets = new Dictionary<string, double> { ["tenant-a"] = budget }, MethodShares = shares, MinimumProbability = floor };

    [Fact]
    public void TheBudgetIsSpentInExpectation_InProportionToTheValues_WithAFloorForEveryone()
    {
        var candidates = Enumerable.Range(0, 200).Select(i => Candidate(i, i % 10 == 0 ? 10 : i % 10 == 5 ? 0 : 1)).ToList();
        var plan = ProbeSelector.Select(candidates, Policy(budget: 25), "w-1");     // small enough that no one is held at 1

        Assert.Equal(25, plan.Expected[("tenant-a", ProbeMethod.Recheck)], 9);
        Assert.Equal(25, plan.Probes.Sum(p => p.Probability), 9);
        Assert.All(plan.Probes, p => Assert.InRange(p.Probability, 0.01, 1.0));                // a zero value is still asked at the floor
        Assert.Equal(0.01, plan.Probes.First(p => p.Candidate.Value == 0).Probability, 12);
        var valuable = plan.Probes.First(p => p.Candidate.Value == 10).Probability;
        var ordinary = plan.Probes.First(p => p.Candidate.Value == 1).Probability;
        Assert.True(valuable > ordinary);
        // Above the floor, proportional: (10 - floor) is ten times (1 - floor) of the same unit.
        Assert.Equal(10, (valuable - 0.01) / (ordinary - 0.01), 6);
    }

    [Fact]
    public void ACandidateWhoseShareWouldPassOne_IsHeldAtOne_AndTheExcessGoesToTheOthers()
    {
        var candidates = new[] { Candidate(0, 1000) }.Concat(Enumerable.Range(1, 50).Select(i => Candidate(i, 1))).ToList();
        var plan = ProbeSelector.Select(candidates, Policy(budget: 10), "w-1");

        Assert.Equal(1.0, plan.Probes[0].Probability);
        Assert.Equal(10, plan.Probes.Sum(p => p.Probability), 9);
        Assert.All(plan.Probes.Skip(1), p => Assert.Equal(plan.Probes[1].Probability, p.Probability, 12));
        Assert.InRange(plan.Probes[1].Probability, 0.17, 0.19);                                // 9 over 50 equal ones

        // A budget that covers everyone asks everyone.
        var all = ProbeSelector.Select(candidates, Policy(budget: 500), "w-1");
        Assert.All(all.Probes, p => Assert.True(p.Drawn && p.Probability == 1.0));
        // All values zero: every candidate is equally worth asking.
        var flat = ProbeSelector.Select(Enumerable.Range(0, 100).Select(i => Candidate(i, 0)).ToList(), Policy(budget: 20), "w-1");
        Assert.All(flat.Probes, p => Assert.Equal(0.2, p.Probability, 12));
    }

    [Fact]
    public void APoolBudgetIsSharedBetweenMethods_ByShareOrEqually_AndEachMethodHasItsOwnProbabilities()
    {
        var candidates = Enumerable.Range(0, 100).SelectMany(i => new[]
        {
            Candidate(i, 1, ProbeMethod.Recheck),
            Candidate(i, 1, ProbeMethod.Repeat),
        }).ToList();

        var equal = ProbeSelector.Select(candidates, Policy(budget: 40), "w-1");
        Assert.Equal(20, equal.Expected[("tenant-a", ProbeMethod.Recheck)], 9);
        Assert.Equal(20, equal.Expected[("tenant-a", ProbeMethod.Repeat)], 9);

        var shared = ProbeSelector.Select(candidates, Policy(budget: 40, shares: new Dictionary<ProbeMethod, double>
        {
            [ProbeMethod.Recheck] = 0.75,
            [ProbeMethod.Repeat] = 0.25,
        }), "w-1");
        Assert.Equal(30, shared.Expected[("tenant-a", ProbeMethod.Recheck)], 9);
        Assert.Equal(10, shared.Expected[("tenant-a", ProbeMethod.Repeat)], 9);
        Assert.Equal(0.3, shared.Probes.First(p => p.Candidate.Method == ProbeMethod.Recheck).Probability, 12);

        // The same item under two methods is two draws, not one.
        var both = shared.Probes.GroupBy(p => p.Candidate.DecisionId).Count(g => g.Select(p => p.Drawn).Distinct().Count() == 2);
        Assert.True(both > 10);
    }

    [Fact]
    public void TheDraw_IsReplayable_AndFollowsTheProbability_AndWeightingBy1OverPiRecoversTheTruth()
    {
        // The value (what makes an item worth asking) is tied to the quantity measured: ten times as likely to be an error.
        var candidates = Enumerable.Range(0, 2000).Select(i => Candidate(i, i % 10 == 0 ? 10 : 1)).ToList();
        var errors = candidates.ToDictionary(c => c.DecisionId, c => c.Value == 10 ? 1.0 : 0.05);
        var truth = errors.Values.Sum();

        var plan = ProbeSelector.Select(candidates, Policy(budget: 200), "w-1");
        Assert.Equal(plan.Drawn.Select(p => p.Candidate.DecisionId), ProbeSelector.Select(candidates, Policy(budget: 200), "w-1").Drawn.Select(p => p.Candidate.DecisionId));
        Assert.InRange(plan.Drawn.Count(), 160, 240);                                         // ~200

        // Prioritizing is not a bias: weighted by 1/π, the drawn errors estimate the total over many plans.
        var estimates = Enumerable.Range(0, 300).Select(v =>
            ProbeSelector.Select(candidates, Policy(budget: 200), $"w-{v}").Drawn.Sum(p => errors[p.Candidate.DecisionId] / p.Probability)).ToList();
        Assert.InRange(estimates.Average() / truth, 0.97, 1.03);
        // …while the unweighted share of drawn errors would over-report them (the priority concentrates on errors).
        var drawn = plan.Drawn.ToList();
        Assert.True(drawn.Average(p => errors[p.Candidate.DecisionId]) > 2 * errors.Values.Average());
    }

    [Fact]
    public void ASelection_RefusesWhatCannotBeWeightedOrPaid()
    {
        var one = new[] { Candidate(0, 1) };
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select([Candidate(0, 1, pool: "elsewhere")], Policy(10), "w"));
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select([Candidate(0, -1)], Policy(10), "w"));
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select([Candidate(0, double.NaN)], Policy(10), "w"));
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select([Candidate(0, 1), Candidate(0, 2)], Policy(10), "w"));
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select(one, Policy(10, shares: new Dictionary<ProbeMethod, double> { [ProbeMethod.Repeat] = 1 }), "w"));   // Recheck has no share
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select(one, Policy(10, shares: new Dictionary<ProbeMethod, double> { [ProbeMethod.Recheck] = 0.7, [ProbeMethod.Repeat] = 0.7 }), "w"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProbeSelector.Select(one, Policy(10, floor: 0), "w"));
        Assert.Throws<ArgumentException>(() => ProbeSelector.Select(one, Policy(10), " "));
        Assert.Empty(ProbeSelector.Select([], Policy(10), "w").Probes);

        // The same item may go to two judges, or by two methods.
        var plan = ProbeSelector.Select([Candidate(0, 1, target: Llm), Candidate(0, 1, target: Person), Candidate(0, 1, ProbeMethod.Plant)], Policy(30), "w");
        Assert.Equal(3, plan.Probes.Count);
    }
}
