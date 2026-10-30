using System;
using System.Collections.Concurrent;

namespace HoneAI.Trust;

/// <summary>
/// Pocock's constant critical value for a window looked at several times: the <c>z</c> for which a standardized
/// running sum of equal-sized increments stays inside ±<c>z</c> at every one of <c>K</c> looks with probability
/// 1 − α. It is the least conservative single <c>z</c> that keeps the chance of an interval missing at any look
/// within α; the union bound (α / K per look) keeps the same promise with a wider interval.
/// </summary>
/// <remarks>
/// The probability is the Armitage–McPherson–Rowe recursion: the density of the running sum on the region it has not
/// left, carried one look to the next by the normal kernel, integrated by the trapezoid rule on a grid that ends
/// exactly on each look's boundary (so the integrand is smooth on every interval and the error is that of the rule).
/// The density is even, so only its positive half is kept. The value is solved by bisection and cached.
/// </remarks>
internal static class PocockBoundary
{
    /// <summary>The most looks solved exactly; beyond it the cost of the recursion is not worth the width it saves.</summary>
    internal const int MaxLooks = 100;

    private const double GridStep = 0.1;
    private const double Tolerance = 1e-4;
    private static readonly double InverseSqrtTwoPi = 1 / Math.Sqrt(2 * Math.PI);
    private static readonly ConcurrentDictionary<(int Looks, double Alpha), double> Cache = new();

    /// <summary>The critical <c>z</c> for <paramref name="looks"/> looks and a family miss probability <paramref name="alpha"/>.</summary>
    internal static double Critical(int looks, double alpha)
        => Cache.GetOrAdd((looks, alpha), key => Solve(key.Looks, key.Alpha));

    /// <summary>P(|Z| &gt; z) for a standard normal Z.</summary>
    internal static double TwoSidedTail(double z) => Erfc(Math.Abs(z) / Math.Sqrt(2));

    /// <summary>
    /// The probability that the running sum leaves ±<paramref name="c"/>·√k at some look k ≤ <paramref name="looks"/>.
    /// </summary>
    internal static double Miss(int looks, double c)
    {
        var bound = c;
        var cells = Cells(bound);
        var x = Grid(bound, cells);
        var density = new double[cells + 1];
        for (var i = 0; i <= cells; i++)
            density[i] = Pdf(x[i]);

        for (var look = 2; look <= looks; look++)
        {
            var nextBound = c * Math.Sqrt(look);
            var nextCells = Cells(nextBound);
            var nextX = Grid(nextBound, nextCells);
            var next = new double[nextCells + 1];
            var h = bound / cells;
            for (var j = 0; j <= nextCells; j++)
            {
                var sum = 0.0;
                for (var i = 0; i <= cells; i++)
                {
                    var weight = i == 0 || i == cells ? 0.5 : 1.0;
                    sum += weight * density[i] * (Pdf(nextX[j] - x[i]) + Pdf(nextX[j] + x[i]));
                }
                next[j] = sum * h;
            }
            bound = nextBound;
            cells = nextCells;
            x = nextX;
            density = next;
        }

        var step = bound / cells;
        var inside = 0.0;
        for (var i = 0; i <= cells; i++)
            inside += (i == 0 || i == cells ? 0.5 : 1.0) * density[i] * step;
        return Math.Max(0, 1 - 2 * inside);
    }

    private static double Solve(int looks, double alpha)
    {
        // One look misses α, so its z is too small; α / K per look misses at most α, so its z is enough.
        var low = Quantile(1 - alpha / 2);
        var high = Quantile(1 - alpha / (2.0 * looks));
        while (high - low > Tolerance)
        {
            var mid = (low + high) / 2;
            if (Miss(looks, mid) > alpha)
                low = mid;
            else
                high = mid;
        }
        return high;
    }

    private static double Quantile(double p) => TrustLedger.NormalQuantile(p);

    private static int Cells(double bound) => Math.Max(1, (int)Math.Ceiling(bound / GridStep));

    private static double[] Grid(double bound, int cells)
    {
        var x = new double[cells + 1];
        for (var i = 0; i <= cells; i++)
            x[i] = bound * i / cells;
        return x;
    }

    private static double Pdf(double x) => InverseSqrtTwoPi * Math.Exp(-0.5 * x * x);

    /// <summary>The complementary error function (Numerical Recipes <c>erfcc</c>, relative error below 1.2e-7).</summary>
    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1 / (1 + 0.5 * z);
        var ans = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418
            + t * (-0.18628806 + t * (0.27886807 + t * (-1.13520398 + t * (1.48851587
            + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? ans : 2 - ans;
    }
}
