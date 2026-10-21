using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI.Learning;

/// <summary>
/// Sends confirmed labels to MLoop as weighted feedback on the prediction the ML tier made: the value, who
/// confirmed it, and <see cref="ConfirmedLabel{TValue}.Weight"/>. MLoop builds the retraining dataset from
/// feedback (<c>mloop sample create --model</c>), so a model retrains on what was confirmed above it, each row
/// counted by its weight. <see cref="IModelLifecycle"/> runs that retraining as a consumer behind this sink.
/// </summary>
/// <typeparam name="TValue">The decision's value type.</typeparam>
public sealed class MLoopFeedbackSink<TValue> : IConfirmedLabelSink<TValue>
{
    /// <summary>
    /// The annotation holding the MLoop prediction id (<see cref="MLoopPredictionResult.PredictionId"/>) of the
    /// ML tier's answer — what feedback is recorded against.
    /// </summary>
    public const string PredictionIdAnnotation = "mloop.predictionId";

    /// <summary>
    /// The annotations that tie a label to the ML tier's MLoop prediction — pass them to
    /// <see cref="LabelConfirmation.Decide{TValue}"/>. Keep them with the decision from the moment the ML tier answers.
    /// </summary>
    /// <exception cref="ArgumentException">MLoop did not log the prediction, so feedback has nothing to attach to.</exception>
    public static IReadOnlyDictionary<string, string> ReferenceTo(MLoopPredictionResult prediction)
    {
        ArgumentNullException.ThrowIfNull(prediction);
        if (string.IsNullOrEmpty(prediction.PredictionId))
            throw new ArgumentException(
                "MLoop returned no prediction id — serve with prediction logging on (Serve:LogPredictions), or feedback cannot find the row.",
                nameof(prediction));
        return new Dictionary<string, string> { [PredictionIdAnnotation] = prediction.PredictionId };
    }

    private readonly IMLoopClient _client;
    private readonly Func<TValue, object> _actualValue;

    /// <param name="client">The MLoop the ML tier predicted with.</param>
    /// <param name="actualValue">The label as MLoop's label column holds it; the value's invariant text when null.</param>
    public MLoopFeedbackSink(IMLoopClient client, Func<TValue, object>? actualValue = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _actualValue = actualValue ?? (v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <inheritdoc />
    public Task SendAsync(ConfirmedLabel<TValue> label, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Annotations?.GetValueOrDefault(PredictionIdAnnotation) is not { Length: > 0 } predictionId)
            throw new InvalidOperationException(
                $"Decision '{label.DecisionId}' carries no '{PredictionIdAnnotation}' annotation — feedback is recorded "
                + "against the MLoop prediction the ML tier made.");

        return _client.RecordFeedbackAsync(
            new MLoopFeedback(predictionId, _actualValue(label.Value!), SourceOf(label), label.Weight),
            cancellationToken);
    }

    /// <summary>
    /// The feedback source: the confirming tier and its version, and whether its trust was measured —
    /// <c>llm-central@3 (default trust)</c>, or the participants of a consensus — <c>llm+person-a (consensus)</c>. Lets a slice of the retraining set be told apart by who labelled it.
    /// </summary>
    public static string SourceOf(ConfirmedLabel<TValue> label)
    {
        ArgumentNullException.ThrowIfNull(label);
        var version = string.IsNullOrEmpty(label.ConfirmedByVersion) ? string.Empty : "@" + label.ConfirmedByVersion;
        var basis = label.TrustBasis switch
        {
            LabelTrustBasis.Default => " (default trust)",
            LabelTrustBasis.Consensus => " (consensus)",
            _ => string.Empty,
        };
        return label.ConfirmedBy + version + basis;
    }
}
