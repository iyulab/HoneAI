using System;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Learning;

/// <summary>
/// Sends each confirmed label to the sink for its share: learning labels to the engine that learns, held-out ones to
/// where the learner is measured from (a consumer's evaluation set).
/// </summary>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed class LabelShareSink<TValue> : IConfirmedLabelSink<TValue>
{
    private readonly IConfirmedLabelSink<TValue> _training;
    private readonly IConfirmedLabelSink<TValue> _evaluation;

    /// <param name="training">Where <see cref="LabelShare.Training"/> labels go — <see cref="MLoopFeedbackSink{TValue}"/>, say.</param>
    /// <param name="evaluation">Where <see cref="LabelShare.Evaluation"/> labels go.</param>
    public LabelShareSink(IConfirmedLabelSink<TValue> training, IConfirmedLabelSink<TValue> evaluation)
    {
        _training = training ?? throw new ArgumentNullException(nameof(training));
        _evaluation = evaluation ?? throw new ArgumentNullException(nameof(evaluation));
    }

    /// <inheritdoc />
    public Task SendAsync(ConfirmedLabel<TValue> label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        return label.Share switch
        {
            LabelShare.Training => _training.SendAsync(label, cancellationToken),
            LabelShare.Evaluation => _evaluation.SendAsync(label, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(label), label.Share, "Unknown label share."),
        };
    }
}
