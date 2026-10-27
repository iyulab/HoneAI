using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>
/// <see cref="IMLoopClient"/> over MLoop's REST API (`mloop serve`). MLoop is reached
/// over the wire, never referenced as an SDK.
/// </summary>
/// <remarks>
/// Construct with an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/>
/// points at the MLoop server and whose default headers carry the JWT bearer token
/// (configure via <c>IHttpClientFactory</c>). The client owns no auth/transport policy —
/// that stays with the consumer.
/// </remarks>
public sealed class HttpMLoopClient : IMLoopClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <param name="http">A configured client (BaseAddress + bearer auth) for the MLoop server.</param>
    public HttpMLoopClient(HttpClient http)
        => _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public async Task<ITracedPrediction<MLoopPredictionResult>> PredictAsync(
        MLoopPredictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MLoop /predict takes an array of feature rows; we send the single row.
        var rows = new[] { request.Features };
        var experiment = ExperimentOf(request.Experiment);
        var uri = "predict" + PredictQuery(request.Model, experiment);

        using var response = await _http.PostAsJsonAsync(uri, rows, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "predict", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        EnsureExperiment(root, experiment);

        if (!root.TryGetProperty("predictions", out var preds)
            || preds.ValueKind != JsonValueKind.Array
            || preds.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("MLoop /predict response carried no predictions.");
        }

        return Traced(root, preds[0]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ITracedPrediction<MLoopPredictionResult>>> PredictImagesAsync(
        MLoopImagePredictionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Images);
        if (request.Images.Count == 0)
            return [];

        var experiment = ExperimentOf(request.Experiment);
        using var response = await _http.PostAsJsonAsync("predict" + PredictQuery(request.Model, experiment), ImagesBody(request.Images), JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "predict", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        EnsureExperiment(root, experiment);
        if (!root.TryGetProperty("predictions", out var preds) || preds.ValueKind != JsonValueKind.Array
            || preds.GetArrayLength() != request.Images.Count)
        {
            throw new InvalidOperationException(
                $"MLoop /predict answered {(preds.ValueKind == JsonValueKind.Array ? preds.GetArrayLength() : 0)} predictions for {request.Images.Count} images.");
        }
        return [.. preds.EnumerateArray().Select(row => Traced(root, row))];
    }

    /// <inheritdoc />
    public async Task<MLoopEmbedding> EmbedAsync(MLoopEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Images);

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Encoder))
            query.Add("encoder=" + Uri.EscapeDataString(request.Encoder));
        if (request.Patches)
            query.Add("patches=true");
        var uri = "embed" + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));
        using var response = await _http.PostAsJsonAsync(uri, ImagesBody(request.Images), JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "embed", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!TryGetProperty(root, "encoder", out var e) || e.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "vectors", out var v) || v.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("MLoop /embed response carried no encoder or vectors.");

        var encoder = EncoderOf(e);
        var vectors = Vectors(v);
        if (vectors.Count != request.Images.Count)
            throw new InvalidOperationException($"MLoop /embed answered {vectors.Count} vectors for {request.Images.Count} images.");
        if (!request.Patches)
            return new MLoopEmbedding(encoder, vectors);

        // A server before MLoop 0.46 ignores ?patches=true and answers vectors only — that is not an answer to this request.
        if (!TryGetProperty(root, "grid", out var g) || g.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "patches", out var p) || p.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("MLoop /embed answered no patches; patch vectors need MLoop 0.46 or later.");
        var grid = new MLoopPatchGrid(
            NumberOf(g, "rows") is { } rows ? (int)rows : 0,
            NumberOf(g, "columns") is { } columns ? (int)columns : 0,
            NumberOf(g, "patchSize") is { } patchSize ? (int)patchSize : 0,
            NumberOf(g, "cropSize") is { } cropSize ? (int)cropSize : 0);
        var patches = p.EnumerateArray().Select(image =>
        {
            if (!TryGetProperty(image, "crop", out var c) || c.ValueKind != JsonValueKind.Object
                || !TryGetProperty(image, "vectors", out var pv) || pv.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("MLoop /embed answered an image's patches without its crop or vectors.");
            var crop = new MLoopImageRegion(NumberOf(c, "x0") ?? 0, NumberOf(c, "y0") ?? 0, NumberOf(c, "x1") ?? 0, NumberOf(c, "y1") ?? 0);
            return new MLoopImagePatches(crop, Vectors(pv));
        }).ToList();
        if (patches.Count != request.Images.Count)
            throw new InvalidOperationException($"MLoop /embed answered patches for {patches.Count} of {request.Images.Count} images.");
        return new MLoopEmbedding(encoder, vectors, grid, patches);
    }

    public async Task<MLoopEmbedding> EmbedTextsAsync(MLoopTextEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Texts);

        var uri = "embed" + (string.IsNullOrWhiteSpace(request.Encoder) ? string.Empty : "?encoder=" + Uri.EscapeDataString(request.Encoder));
        using var response = await _http.PostAsJsonAsync(uri, new Dictionary<string, IReadOnlyList<string>> { ["texts"] = request.Texts }, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "embed", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        if (!TryGetProperty(root, "encoder", out var e) || e.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "vectors", out var v) || v.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("MLoop /embed response carried no encoder or vectors.");
        var vectors = Vectors(v);
        if (vectors.Count != request.Texts.Count)
            throw new InvalidOperationException($"MLoop /embed answered {vectors.Count} vectors for {request.Texts.Count} texts.");
        return new MLoopEmbedding(EncoderOf(e), vectors, Truncated: NumberOf(root, "truncated") is { } t ? (int)t : null);
    }

    // The encoder an /embed answer names — the identity of its vectors' space.
    private static MLoopEncoder EncoderOf(JsonElement e) => new(
        String(e, "id") ?? throw new InvalidOperationException("MLoop /embed response named no encoder id."),
        String(e, "version") ?? string.Empty,
        String(e, "sha256"),
        NumberOf(e, "dimension") is { } d ? (int)d : 0,
        TryGetProperty(e, "normalized", out var n) && n.ValueKind == JsonValueKind.True,
        String(e, "preprocessor"),
        String(e, "kind"),
        String(e, "tokenizerSha256"));

    private static List<float[]> Vectors(JsonElement array) =>
        [.. array.EnumerateArray().Select(vector => vector.EnumerateArray().Select(x => x.GetSingle()).ToArray())];

    // MLoop's image input: {"images": ["<base64>", …]} — the same body /predict and /embed take.
    private static Dictionary<string, string[]> ImagesBody(IReadOnlyList<ReadOnlyMemory<byte>> images)
        => new() { ["images"] = [.. images.Select(image => Convert.ToBase64String(image.Span))] };

    // One answered row as a traced prediction: its outputs, MLoop's confidence, and the id it was logged under.
    private static TracedPrediction<MLoopPredictionResult> Traced(JsonElement root, JsonElement row)
    {
        var outputs = ToObjectMap(row);
        var provenance = new PredictionProvenance
        {
            Kind = JudgeKind.Ml,
            Confidence = ConfidenceOf(row),
            Rationale = root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String
                ? $"mloop:{t.GetString()}"
                : "mloop",
        };

        // The logged id is not an output of the model: it names the row, so feedback can find it.
        outputs.Remove("predictionId");
        var predictionId = String(row, "predictionId");
        return new TracedPrediction<MLoopPredictionResult>(new MLoopPredictionResult(outputs, predictionId, ModelOf(root)), provenance);
    }

    // The answering model, from the envelope: one model answers a whole request.
    private static MLoopModel? ModelOf(JsonElement root)
    {
        var experimentId = String(root, "experimentId");
        var sha256 = String(root, "modelSha256");
        return experimentId is null && sha256 is null ? null : new MLoopModel(experimentId, sha256);
    }

    /// <inheritdoc />
    public async Task<ITracedPrediction<MLoopForecastResult>> ForecastAsync(
        MLoopForecastRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Forecasting is horizon-based (MLoop 0.20+ contract): a JSON *object* body
        // ({} = the model's trained horizon), unlike every other task's row-array body.
        var body = request.Horizon is { } horizon
            ? new Dictionary<string, object?> { ["horizon"] = horizon }
            : new Dictionary<string, object?>();
        var uri = "predict" + NameQuery(request.Model);

        using var response = await _http.PostAsJsonAsync(uri, body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "predict", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("predictions", out var preds)
            || preds.ValueKind != JsonValueKind.Array
            || preds.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("MLoop /predict response carried no forecast points.");
        }

        var points = new List<MLoopForecastPoint>(preds.GetArrayLength());
        var minConfidence = 1.0;
        foreach (var row in preds.EnumerateArray())
        {
            points.Add(new MLoopForecastPoint(
                Value: NumberOf(row, "score") ?? 0.0,
                LowerBound: NumberOf(row, "scoreLowerBound"),
                UpperBound: NumberOf(row, "scoreUpperBound"),
                IntervalConfidence: NumberOf(row, "intervalConfidence")));
            minConfidence = Math.Min(minConfidence, ConfidenceOf(row));
        }

        var provenance = new PredictionProvenance
        {
            Kind = JudgeKind.Ml,
            // A forecast is one multi-step answer; its confidence is its weakest step's —
            // later steps widen their band, and the chain is only as strong as that.
            Confidence = minConfidence,
            Rationale = "mloop:forecasting",
        };

        return new TracedPrediction<MLoopForecastResult>(new MLoopForecastResult(points, ModelOf(root)), provenance);
    }

    /// <inheritdoc />
    public async Task<MLoopJob> TrainAsync(MLoopTrainRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // MLoop /train requires labelColumn + task as non-null strings; forward what we have.
        var body = new Dictionary<string, object?>
        {
            ["dataFile"] = request.DataPath,
            ["labelColumn"] = request.Label ?? string.Empty,
            ["task"] = request.Task ?? string.Empty,
            ["name"] = request.Model,
        };
        if (request.AdditionalDataPaths is { Count: > 0 } more)
            body["additionalDataFiles"] = more;
        if (!string.IsNullOrWhiteSpace(request.Weight))
            body["weight"] = request.Weight;
        if (!request.Promote)
            body["promote"] = false;
        // Forwarded only when set: an absent field leaves the choice to the project's mloop.yaml, as an absent CLI option does.
        if (!string.IsNullOrWhiteSpace(request.Balance))
            body["balance"] = request.Balance;
        if (request.TimeLimitSeconds is { } seconds)
            body["timeLimitSeconds"] = seconds;
        if (!string.IsNullOrWhiteSpace(request.Metric))
            body["metric"] = request.Metric;
        if (request.TestSplit is { } split)
            body["testSplit"] = split;
        if (request.MaxRows is { } maxRows)
            body["maxRows"] = maxRows;

        using var response = await _http.PostAsJsonAsync("train", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "train", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var jobId = doc.RootElement.TryGetProperty("jobId", out var j) ? j.GetString() : null;

        return new MLoopJob(jobId ?? string.Empty, response.StatusCode == HttpStatusCode.Accepted ? "queued" : "unknown");
    }

    /// <inheritdoc />
    public async Task<MLoopJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var response = await _http.GetAsync("jobs/" + Uri.EscapeDataString(jobId), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(response, "jobs", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var id = root.TryGetProperty("jobId", out var j) && j.ValueKind == JsonValueKind.String ? j.GetString()! : jobId;
        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "unknown";
        var experimentId = root.TryGetProperty("experimentId", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;
        var metrics = ParseMetrics(root);
        bool? promoted = root.TryGetProperty("promoted", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? p.GetBoolean()
            : null;

        IReadOnlyList<string>? warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
            ? [.. w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : null;

        return new MLoopJob(id, status, experimentId, metrics, promoted,
            String(root, "keptInStagingReason"), String(root, "statusMessage"), warnings);
    }

    /// <inheritdoc />
    public async Task PromoteAsync(string model, string experimentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(experimentId);

        var body = new Dictionary<string, object?> { ["experimentId"] = experimentId, ["name"] = model };

        using var response = await _http.PostAsJsonAsync("promote", body, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "promote", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MLoopRollback> RollbackAsync(string model, string? to = null, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?> { ["name"] = model, ["to"] = to };
        using var response = await _http.PostAsJsonAsync("rollback", body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "rollback", cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        return new MLoopRollback(
            String(root, "experimentId") ?? throw new MLoopClientException("MLoop /rollback answered without an experimentId.", response.StatusCode),
            String(root, "previousExperimentId"));
    }

    /// <inheritdoc />
    public async Task<MLoopEvaluation> EvaluateAsync(MLoopEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TestDataPath, nameof(request));
        var body = new Dictionary<string, object?>
        {
            ["testDataPath"] = request.TestDataPath,
            ["experimentId"] = request.ExperimentId,
            ["name"] = request.Model,
            ["weight"] = request.Weight,
            ["against"] = request.Against,
        };
        using var response = await _http.PostAsJsonAsync("evaluate", body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "evaluate", cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        // A model with nothing in production answers 200 with status "skipped" when no experiment was named.
        if (String(root, "status") == "skipped")
            throw new MLoopClientException($"MLoop /evaluate skipped: {String(root, "reason")}", response.StatusCode);

        string? against = null, notPaired = null;
        MLoopPairedComparison? paired = null;
        if (root.TryGetProperty("comparison", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            against = String(c, "against");
            notPaired = String(c, "notPaired");
            if (c.TryGetProperty("paired", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                paired = new MLoopPairedComparison(
                    String(p, "measure") ?? string.Empty,
                    p.TryGetProperty("rows", out var rows) && rows.TryGetInt32(out var n) ? n : 0,
                    NumberOf(p, "candidate") ?? double.NaN,
                    NumberOf(p, "baseline") ?? double.NaN,
                    NumberOf(p, "difference") ?? double.NaN,
                    NumberOf(p, "low") ?? double.NaN,
                    NumberOf(p, "high") ?? double.NaN,
                    String(p, "verdict") ?? string.Empty);
            }
        }
        return new MLoopEvaluation(
            String(root, "experimentId") ?? request.ExperimentId ?? string.Empty,
            ParseMetrics(root) ?? new Dictionary<string, double>(),
            ParseConfusion(root),
            against,
            paired,
            notPaired,
            Strings(root, "warnings"));
    }

    private static MLoopConfusion? ParseConfusion(JsonElement root)
    {
        if (!root.TryGetProperty("confusion", out var c) || c.ValueKind != JsonValueKind.Object
            || !c.TryGetProperty("classes", out var classes) || classes.ValueKind != JsonValueKind.Array
            || !c.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Array)
            return null;
        var names = classes.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();
        var matrix = counts.EnumerateArray()
            .Select(row => (IReadOnlyList<double>)row.EnumerateArray().Select(v => v.GetDouble()).ToList())
            .ToList();
        return matrix.Count == names.Count && matrix.All(r => r.Count == names.Count) ? new MLoopConfusion(names, matrix) : null;
    }

    private static IReadOnlyList<string>? Strings(JsonElement root, string name)
        => root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : null;

    /// <inheritdoc />
    public async Task<MLoopModelInfo?> GetInfoAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("info" + NameQuery(model), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccess(response, "info", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : model ?? "default";
        var task = root.TryGetProperty("task", out var tk) && tk.ValueKind == JsonValueKind.String
            ? tk.GetString()
            : null;

        return new MLoopModelInfo(name, task, ParseMetrics(root), String(root, "experimentId"));
    }

    /// <inheritdoc />
    public async Task<MLoopSample> CreateSampleAsync(MLoopSampleRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new Dictionary<string, object?>
        {
            ["name"] = request.Model,
            ["rows"] = request.Rows,
            ["strategy"] = request.Strategy,
        };
        using var response = await _http.PostAsJsonAsync("samples", body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "samples", cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        return new MLoopSample(
            String(root, "path") ?? throw new InvalidOperationException("MLoop /samples response carried no path."),
            root.TryGetProperty("sampledCount", out var n) && n.TryGetInt32(out var rows) ? rows : 0,
            root.TryGetProperty("labeledCount", out var l) && l.TryGetInt32(out var labeled) ? labeled : 0,
            String(root, "labelColumn"),
            String(root, "weightColumn"));
    }

    /// <inheritdoc />
    public async Task RecordFeedbackAsync(MLoopFeedback feedback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feedback);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedback.PredictionId);

        var body = new Dictionary<string, object?>
        {
            ["predictionId"] = feedback.PredictionId,
            ["actualValue"] = feedback.ActualValue,
            ["source"] = feedback.Source,
            ["weight"] = feedback.Weight,
        };
        using var response = await _http.PostAsJsonAsync("feedback", body, JsonOptions, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "feedback", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MLoopDrift> GetDriftAsync(MLoopDriftRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Model))
            query.Add("name=" + Uri.EscapeDataString(request.Model));
        if (request.Rows is { } rows)
            query.Add("rows=" + rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(request.SliceBy))
            query.Add("sliceBy=" + Uri.EscapeDataString(request.SliceBy));
        var uri = "drift" + (query.Count == 0 ? string.Empty : "?" + string.Join("&", query));

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccess(response, "drift", cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        Dictionary<string, MLoopDriftReport>? slices = null;
        if (root.TryGetProperty("slices", out var s) && s.ValueKind == JsonValueKind.Array)
        {
            slices = new Dictionary<string, MLoopDriftReport>(StringComparer.Ordinal);
            foreach (var slice in s.EnumerateArray())
                if (String(slice, "value") is { } value && ParseDriftReport(slice) is { } report)
                    slices[value] = report;
        }
        return new MLoopDrift(
            String(root, "status") ?? "Unknown",
            String(root, "experimentId"),
            String(root, "message") ?? string.Empty,
            ParseDriftReport(root),
            slices);
    }

    private static MLoopDriftReport? ParseDriftReport(JsonElement holder)
    {
        if (!holder.TryGetProperty("drift", out var d) || d.ValueKind != JsonValueKind.Object)
            return null;
        var features = d.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(ParseColumnDrift).OfType<MLoopColumnDrift>().ToList()
            : [];
        var prediction = d.TryGetProperty("prediction", out var p) ? ParseColumnDrift(p) : null;
        return new MLoopDriftReport(
            d.TryGetProperty("rows", out var r) && r.TryGetInt32(out var n) ? n : 0,
            Level(String(d, "level")),
            features,
            prediction);
    }

    private static MLoopColumnDrift? ParseColumnDrift(JsonElement e)
        => e.ValueKind == JsonValueKind.Object && String(e, "column") is { } column
            ? new MLoopColumnDrift(column, e.TryGetProperty("psi", out var psi) && psi.TryGetDouble(out var x) ? x : 0, Level(String(e, "level")))
            : null;

    // An unknown level reads as the highest: a newer server's word for "moved" must not read as "stable".
    private static MLoopDriftLevel Level(string? text)
        => Enum.TryParse<MLoopDriftLevel>(text, ignoreCase: true, out var level) && Enum.IsDefined(level) ? level : MLoopDriftLevel.Significant;

    private static string? String(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IReadOnlyDictionary<string, double>? ParseMetrics(JsonElement root)
    {
        if (!root.TryGetProperty("metrics", out var m) || m.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, double>();
        foreach (var prop in m.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var d))
                map[prop.Name] = d;
        }
        return map.Count > 0 ? map : null;
    }

    private static string NameQuery(string? model)
        => string.IsNullOrWhiteSpace(model) ? string.Empty : "?name=" + Uri.EscapeDataString(model);

    // /predict's query: the model by name, and the experiment that answers instead of production (a shadow prediction).
    private static string PredictQuery(string? model, string? experiment)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(model))
            query.Add("name=" + Uri.EscapeDataString(model));
        if (experiment is not null)
            query.Add("experiment=" + Uri.EscapeDataString(experiment));
        return query.Count == 0 ? string.Empty : "?" + string.Join('&', query);
    }

    // The server trims the id it is given, and answers with the trimmed one; an empty id would silently be production.
    private static string? ExperimentOf(string? experiment)
    {
        if (experiment is null)
            return null;
        ArgumentException.ThrowIfNullOrWhiteSpace(experiment, nameof(experiment));
        return experiment.Trim();
    }

    // A shadow prediction the server answered with another model — an older server that does not read `experiment` serves
    // production — would be recorded as the candidate's answer. Refuse it rather than let the ledger believe it.
    private static void EnsureExperiment(JsonElement root, string? requested)
    {
        if (requested is null)
            return;
        var answered = String(root, "experimentId");
        if (!string.Equals(answered, requested, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"MLoop /predict was asked to answer with experiment '{requested}' and answered with "
                + (answered is null ? "no named experiment" : $"'{answered}'")
                + " — the server may predate experiment-specific prediction; its answer is not the candidate's.");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, string op, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new MLoopClientException($"MLoop /{op} failed ({(int)response.StatusCode}): {body}", response.StatusCode);
    }

    private static Dictionary<string, object?> ToObjectMap(JsonElement obj)
    {
        var map = new Dictionary<string, object?>();
        if (obj.ValueKind != JsonValueKind.Object)
            return map;
        foreach (var prop in obj.EnumerateObject())
            map[prop.Name] = ToObject(prop.Value);
        return map;
    }

    private static object? ToObject(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Object => ToObjectMap(e),
        JsonValueKind.Array => e.EnumerateArray().Select(ToObject).ToArray(),
        _ => e.ToString(),
    };

    /// <summary>
    /// Conservative confidence from a prediction row, clamped to a finite [0,1] (trust boundary on external
    /// model output). MLoop now owns the normalized confidence rule (<c>ConfidencePolicy</c>) and returns it
    /// as a per-row <c>confidence</c> field, so this prefers that field and only falls back to deriving it
    /// locally for an older server that doesn't send it — the derivation below is transitional and can be
    /// dropped once the minimum MLoop version is enforced. Fallback rules (mirroring what MLoop computes):
    /// <list type="bullet">
    /// <item>classification → max class probability;</item>
    /// <item>anomaly detection → distance from the decision boundary, <c>|anomalyScore - 0.5| * 2</c>
    /// (RandomizedPca <c>Score ∈ [0,1]</c> with default threshold 0.5 — Microsoft.ML docs). Confidence
    /// is in the anomaly/normal DECISION, so a strong anomaly and a strong inlier are both confident;
    /// only boundary scores are uncertain. Mapping the score directly would wrongly read a confident
    /// inlier (low score) as low-confidence and escalate every normal row;</item>
    /// <item>regression with a conformal band → <c>1 − min(halfWidth / |Score|, 1)</c>: a narrow band
    /// (model certain) is high-confidence, a wide band (uncertain) low. The raw <c>Score</c> is the
    /// predicted target value, not a confidence, and clamping it to [0,1] would be meaningless
    /// (a predicted 12.5 read as confidence 1.0). The heteroscedastic per-row band width drives
    /// escalation instead. Width is taken relative to the prediction magnitude so the mapping is
    /// dataset-scale-agnostic (the <c>|Score|</c> normalizer is a heuristic);</item>
    /// <item>other scalar score (ranking/recommendation, no band) → the score itself;</item>
    /// <item>otherwise → 0.</item>
    /// </list>
    /// </summary>
    private static double ConfidenceOf(JsonElement row)
    {
        double raw = 0.0;
        if (row.ValueKind == JsonValueKind.Object)
        {
            // Prefer MLoop's normalized confidence (single authority) when the server provides it.
            if (TryGetProperty(row, "confidence", out var conf)
                && conf.ValueKind == JsonValueKind.Number && conf.TryGetDouble(out var c))
            {
                return double.IsFinite(c) ? Math.Clamp(c, 0.0, 1.0) : 0.0;
            }

            if (TryGetProperty(row, "probabilities", out var probs) && probs.ValueKind == JsonValueKind.Object)
            {
                var max = double.NegativeInfinity;
                foreach (var p in probs.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v) && v > max)
                        max = v;
                if (!double.IsNegativeInfinity(max))
                    raw = max;
            }
            else if (TryGetProperty(row, "anomalyScore", out var anomaly)
                     && anomaly.ValueKind == JsonValueKind.Number && anomaly.TryGetDouble(out var a))
            {
                raw = Math.Abs(a - 0.5) * 2.0;
            }
            else if (TryGetProperty(row, "scoreUpperBound", out var upperEl)
                     && upperEl.ValueKind == JsonValueKind.Number && upperEl.TryGetDouble(out var upper)
                     && TryGetProperty(row, "scoreLowerBound", out var lowerEl)
                     && lowerEl.ValueKind == JsonValueKind.Number && lowerEl.TryGetDouble(out var lower))
            {
                // Regression: confidence from the conformal band width, not the raw Score.
                double half = Math.Abs(upper - lower) / 2.0;
                double point = TryGetProperty(row, "score", out var sc)
                               && sc.ValueKind == JsonValueKind.Number && sc.TryGetDouble(out var sv)
                    ? sv
                    : (upper + lower) / 2.0;
                double denom = Math.Max(Math.Abs(point), 1e-9);
                raw = 1.0 - Math.Min(half / denom, 1.0);
            }
            else if (TryGetProperty(row, "score", out var score)
                     && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var s))
            {
                raw = s;
            }
        }
        return double.IsFinite(raw) ? Math.Clamp(raw, 0.0, 1.0) : 0.0;
    }

    /// <summary>Case-insensitive numeric property read; null when absent or non-numeric.</summary>
    private static double? NumberOf(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && TryGetProperty(obj, name, out var el)
           && el.ValueKind == JsonValueKind.Number
           && el.TryGetDouble(out var v)
           && double.IsFinite(v)
            ? v
            : null;

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

/// <summary>Raised when an MLoop HTTP request returns a non-success status.</summary>
public sealed class MLoopClientException : Exception
{
    public MLoopClientException(string message, HttpStatusCode statusCode) : base(message)
        => StatusCode = statusCode;

    /// <summary>The HTTP status returned by the MLoop server.</summary>
    public HttpStatusCode StatusCode { get; }
}
