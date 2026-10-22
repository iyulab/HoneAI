using System;
using System.Linq;
using HoneAI;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class LedgerShrinkageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

    private static LedgerCell Cell(string slice, double? agreement, double n, string tier = "ml")
        => new()
        {
            TierId = tier,
            SliceKey = slice,
            WindowStart = Now.AddDays(-7),
            WindowEnd = Now,
            Rechecks = (int)n,
            UpperFailures = 0,
            EffectiveSampleSize = n,
            Agreement = agreement,
            MissesEstimated = 0,
            Caught = 0,
            Confidence = 0.95,
        };

    private static string? GroupOf(string slice) => slice.Split(':') is [var group, _] ? group : null;

    [Fact]
    public void AlikeSlices_LendTheirStrength_ToASmallOne()
    {
        // Five slices of 100 rechecks at 0.9, and one of 4 at 0.5 — four rechecks say little; its siblings say a lot.
        var cells = Enumerable.Range(1, 5).Select(i => Cell($"g:s{i}", 0.9, 100)).Append(Cell("g:small", 0.5, 4)).ToList();

        var shrunk = LedgerShrinkage.Agreement(cells, GroupOf).ToDictionary(s => s.SliceKey);

        Assert.InRange(shrunk["g:small"].Agreement!.Value, 0.85, 0.9);
        Assert.InRange(shrunk["g:s1"].Agreement!.Value, 0.89, 0.91);
        Assert.Equal(0.5, shrunk["g:small"].Raw);
    }

    [Fact]
    public void UnlikeSlices_KeepTheirOwnEstimates()
    {
        var cells = new[] { Cell("a", 0.5, 200), Cell("b", 0.99, 200), Cell("c", 0.75, 200) };

        var shrunk = LedgerShrinkage.Agreement(cells, GroupOf).ToDictionary(s => s.SliceKey);

        Assert.True(shrunk["a"].PriorStrength < 20, $"κ = {shrunk["a"].PriorStrength}");
        Assert.InRange(shrunk["a"].Agreement!.Value, 0.5, 0.53);
        Assert.InRange(shrunk["b"].Agreement!.Value, 0.97, 0.99);
    }

    [Fact]
    public void ASmallSlice_IsPulledTowardItsOwnGroup_NotTheWhole()
    {
        var cells = new[]
        {
            Cell("good:1", 0.95, 300), Cell("good:2", 0.96, 300),
            Cell("poor:1", 0.60, 300), Cell("poor:2", 0.62, 300), Cell("poor:new", 1.0, 2),
        };

        var shrunk = LedgerShrinkage.Agreement(cells, GroupOf).ToDictionary(s => s.SliceKey);

        Assert.Equal("poor", shrunk["poor:new"].Group);
        Assert.InRange(shrunk["poor:new"].Toward!.Value, 0.58, 0.64);
        Assert.InRange(shrunk["poor:new"].Agreement!.Value, 0.58, 0.66);   // two agreeing rechecks do not lift it to 1
    }

    [Fact]
    public void ASliceWithNothingAnswered_TakesTheLevelAbove_AndNothingAnywhereIsNull()
    {
        var shrunk = LedgerShrinkage.Agreement([Cell("g:1", 0.8, 50), Cell("g:2", 0.9, 50), Cell("g:cold", null, 0)], GroupOf)
            .ToDictionary(s => s.SliceKey);
        Assert.Equal(shrunk["g:cold"].Toward, shrunk["g:cold"].Agreement);
        Assert.Null(shrunk["g:cold"].Raw);

        var empty = Assert.Single(LedgerShrinkage.Agreement([Cell("cold", null, 0)], GroupOf));
        Assert.Null(empty.Agreement);
    }

    [Fact]
    public void CellsOfTwoTiers_OrASliceTwice_AreRefused()
    {
        Assert.Throws<ArgumentException>(() => LedgerShrinkage.Agreement([Cell("a", 0.9, 10), Cell("b", 0.9, 10, tier: "llm")], GroupOf));
        Assert.Throws<ArgumentException>(() => LedgerShrinkage.Agreement([Cell("a", 0.9, 10), Cell("a", 0.8, 10)], GroupOf));
    }
}
