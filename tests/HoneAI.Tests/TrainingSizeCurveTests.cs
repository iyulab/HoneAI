using System;
using System.Linq;
using HoneAI.Learning;
using Xunit;

namespace HoneAI.Tests;

public class TrainingSizeCurveTests
{
    private static SizeMeasurement At(double size, double metric, double se = 0.01) => new(size, metric, se);

    [Fact]
    public void ARisingThenFlatCurve_SettlesWhereTheGainStops()
    {
        var curve = new[] { At(100, 0.70), At(200, 0.78), At(400, 0.84), At(800, 0.845), At(1600, 0.846), At(3200, 0.847) };

        var pick = TrainingSizeCurve.Recommend(curve);

        Assert.Equal(400, pick.Size);                                          // 800 and beyond are within noise of 400
        Assert.True(pick.Plateaued);
        Assert.Contains("no size above 400", pick.Reason);
    }

    [Fact]
    public void AStillRisingCurve_AsksForTheLargestMeasured_AndSaysItCannotSeeAPlateau()
    {
        var pick = TrainingSizeCurve.Recommend([At(100, 0.60), At(200, 0.70), At(400, 0.80)]);

        Assert.Equal(400, pick.Size);
        Assert.False(pick.Plateaued);
        Assert.Contains("largest measured", pick.Reason);
        Assert.False(TrainingSizeCurve.Recommend([At(100, 0.6)]).Plateaued);
    }

    [Fact]
    public void ANoisyMeasurement_NeedsMoreProofOfAGain_AndLowerIsBetterReadsTheOtherWay()
    {
        // A gain of 0.03 is shown with se 0.01, not with se 0.05.
        Assert.Equal(100, TrainingSizeCurve.Recommend([At(100, 0.70, 0.05), At(200, 0.73, 0.05)]).Size);
        Assert.Equal(200, TrainingSizeCurve.Recommend([At(100, 0.70, 0.01), At(200, 0.73, 0.01)]).Size);
        // A larger confidence asks more proof: the same curve settles smaller.
        Assert.Equal(100, TrainingSizeCurve.Recommend([At(100, 0.70, 0.01), At(200, 0.73, 0.01)], confidence: 0.9999).Size);

        // A loss: lower is better, so a falling curve is the improving one.
        var loss = new[] { At(100, 0.90), At(200, 0.60), At(400, 0.45), At(800, 0.449) };
        Assert.Equal(400, TrainingSizeCurve.Recommend(loss, higherIsBetter: false).Size);
    }

    [Fact]
    public void TheOrderTheyWereMeasuredIn_DoesNotMatter_AndBadInputIsRefused()
    {
        var curve = new[] { At(100, 0.70), At(200, 0.78), At(400, 0.84), At(800, 0.845) };
        Assert.Equal(TrainingSizeCurve.Recommend(curve), TrainingSizeCurve.Recommend(curve.Reverse()));

        Assert.Throws<ArgumentException>(() => TrainingSizeCurve.Recommend([]));
        Assert.Throws<ArgumentException>(() => TrainingSizeCurve.Recommend([At(100, 0.7), At(100, 0.8)]));
        Assert.Throws<ArgumentException>(() => TrainingSizeCurve.Recommend([At(0, 0.7)]));
        Assert.Throws<ArgumentException>(() => TrainingSizeCurve.Recommend([At(100, 0.7, -1)]));
        Assert.Throws<ArgumentException>(() => TrainingSizeCurve.Recommend([At(100, double.NaN)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrainingSizeCurve.Recommend([At(100, 0.7)], confidence: 0.5));
    }
}
