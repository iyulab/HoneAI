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
/// <see cref="IMLoopClient"/> over MLoop's REST API (`mloop serve`) — the HTTP half of
/// the transport unification (back-derivation §3.5 ④, generalizing U-Vision's
/// <c>MloopClassifier</c>). MLoop is reached over the wire, never referenced as an SDK.
/// </summary>
/// <remarks>
/// Construct with an <see cref="HttpClient"/> whose <see cref="HttpClient.BaseAddress"/>
/// points at the MLoop server and whose default headers carry the JWT bearer token
/// (configure via <c>IHttpClientFactory</c>). The client owns no auth/transport policy —
/// that stays with the consumer, matching how both consumers wire it today.
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
        var uri = "predict" + NameQuery(request.Model);

        using var response = await _http.PostAsJsonAsync(uri, rows, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccess(response, "predict", cancellationToken).ConfigureAwait(false);

        using var doc = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("predictions", out var preds)
            || preds.ValueKind != JsonValueKind.Array
            || preds.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("MLoop /predict response carried no predictions.");
        }

        var row = preds[0];
        var outputs = ToObjectMap(row);
        var provenance = new PredictionProvenance
        {
            SourceLayer = ReasoningLayer.AutoMl,
            Confidence = ConfidenceOf(row),
            Rationale = root.TryGetProperty("task", out var t) && t.ValueKind == JsonValueKind.String
                ? $"mloop:{t.GetString()}"
                : "mloop",
        };

        return new TracedPrediction<MLoopPredictionResult>(new MLoopPredictionResult(outputs), provenance);
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

        return new MLoopJob(id, status, experimentId, metrics);
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

        return new MLoopModelInfo(name, task, ParseMetrics(root));
    }

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
    /// Conservative confidence from a prediction row: max class probability, else the
    /// score, else 0 — clamped to a finite [0,1] (trust boundary on external model output,
    /// mirroring U-Vision's <c>ConfidenceOf</c>).
    /// </summary>
    private static double ConfidenceOf(JsonElement row)
    {
        double raw = 0.0;
        if (row.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(row, "probabilities", out var probs) && probs.ValueKind == JsonValueKind.Object)
            {
                var max = double.NegativeInfinity;
                foreach (var p in probs.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v) && v > max)
                        max = v;
                if (!double.IsNegativeInfinity(max))
                    raw = max;
            }
            else if (TryGetProperty(row, "score", out var score)
                     && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var s))
            {
                raw = s;
            }
        }
        return double.IsFinite(raw) ? Math.Clamp(raw, 0.0, 1.0) : 0.0;
    }

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
