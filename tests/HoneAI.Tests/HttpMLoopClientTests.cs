using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Unit tests for <see cref="HttpMLoopClient"/> against a stub <see cref="HttpMessageHandler"/>.
/// The canned responses mirror MLoop's actual REST shapes (grounded against
/// MLoop.API/Program.cs): /predict → {predictions:[…]}, /train → 202 {jobId},
/// /promote → 200, /info → {name,task,metrics}.
/// </summary>
public class HttpMLoopClientTests
{
    private static HttpMLoopClient Client(StubHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://mloop.local/") });

    [Fact]
    public async Task Predict_MapsRowToOutputsAndAttachesAutoMlProvenance()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "modelName": "weld",
              "task": "binary",
              "predictions": [
                { "predictedLabel": "NG", "probabilities": { "OK": 0.18, "NG": 0.82 } }
              ]
            }
            """));
        var client = Client(handler);

        var pred = await client.PredictAsync(new MLoopPredictionRequest(
            new Dictionary<string, object?> { ["temp"] = 36.5 }, Model: "weld"));

        Assert.Equal("NG", pred.Value.Outputs["predictedLabel"]);
        Assert.Equal(JudgeKind.Ml, pred.Provenance.Kind);
        Assert.Equal(0.82, pred.Provenance.Confidence!.Value, precision: 6);
        Assert.Equal("mloop:binary", pred.Provenance.Rationale);

        // Request shape: POST predict?name=weld with the single feature row as an array.
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("predict?name=weld", handler.LastUri);
        Assert.Contains("\"temp\":36.5", handler.LastBody);
        Assert.StartsWith("[", handler.LastBody!.TrimStart());
    }

    [Fact]
    public async Task Predict_SurfacesTheLoggedPredictionId_NotAsAnOutput()
    {
        // MLoop logs a served row and returns the id feedback is recorded against.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "task": "binary", "predictions": [ { "predictedLabel": "NG", "predictionId": "9f2c4e1a7b3d5f60" } ] }
            """));

        var pred = await Client(handler).PredictAsync(new MLoopPredictionRequest(new Dictionary<string, object?> { ["t"] = 1 }));

        Assert.Equal("9f2c4e1a7b3d5f60", pred.Value.PredictionId);
        Assert.False(pred.Value.Outputs.ContainsKey("predictionId"));
    }

    [Fact]
    public async Task RecordFeedback_PostsTheValueSourceAndWeight()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "recorded": true }"""));

        await Client(handler).RecordFeedbackAsync(new MLoopFeedback("9f2c4e1a7b3d5f60", "NG", "person-a@1", 4.0));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.EndsWith("feedback", handler.LastUri);
        Assert.Contains("\"predictionId\":\"9f2c4e1a7b3d5f60\"", handler.LastBody);
        Assert.Contains("\"actualValue\":\"NG\"", handler.LastBody);
        Assert.Contains("\"source\":\"person-a@1\"", handler.LastBody);
        Assert.Contains("\"weight\":4", handler.LastBody);
    }

    [Fact]
    public async Task CreateSample_PostsTheRequest_AndReadsTheFile()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "modelName": "weld", "path": ".mloop/samples/weld_20261021.csv", "sampledCount": 10, "labeledCount": 4,
              "totalAvailable": 12, "strategy": "FeedbackPriority", "labelColumn": "y", "weightColumn": "weight" }
            """));

        var sample = await Client(handler).CreateSampleAsync(new MLoopSampleRequest("weld", 10, "feedback-priority"));

        Assert.Equal(new MLoopSample(".mloop/samples/weld_20261021.csv", 10, 4, "y", "weight"), sample);
        Assert.EndsWith("samples", handler.LastUri);
        Assert.Contains("\"strategy\":\"feedback-priority\"", handler.LastBody);
    }

    [Fact]
    public async Task Train_SendsAdditionalDataFiles_OnlyWhenThereAreAny()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, """{ "jobId": "job-1" }"""));
        var client = Client(handler);

        await client.TrainAsync(new MLoopTrainRequest("d.csv", "y", "binary-classification", "weld", [".mloop/samples/s.csv"], "weight"));
        Assert.Contains("\"additionalDataFiles\":[\".mloop/samples/s.csv\"]", handler.LastBody);
        Assert.Contains("\"weight\":\"weight\"", handler.LastBody);

        await client.TrainAsync(new MLoopTrainRequest("d.csv", "y", "binary-classification"));
        Assert.DoesNotContain("additionalDataFiles", handler.LastBody);
        Assert.DoesNotContain("promote", handler.LastBody);                // MLoop's default: it promotes

        await client.TrainAsync(new MLoopTrainRequest("d.csv", "y", "binary-classification", Promote: false));
        Assert.Contains("\"promote\":false", handler.LastBody);
    }

    [Fact]
    public async Task Train_ForwardsBalanceBudgetMetricSplitAndRows_OnlyWhenSet()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, """{ "jobId": "job-1" }"""));
        var client = Client(handler);

        await client.TrainAsync(new MLoopTrainRequest("d.csv", "y", "binary-classification",
            Balance: "auto", TimeLimitSeconds: 120, Metric: "auc", TestSplit: 0.25, MaxRows: 50000));
        Assert.Contains("\"balance\":\"auto\"", handler.LastBody);
        Assert.Contains("\"timeLimitSeconds\":120", handler.LastBody);
        Assert.Contains("\"metric\":\"auc\"", handler.LastBody);
        Assert.Contains("\"testSplit\":0.25", handler.LastBody);
        Assert.Contains("\"maxRows\":50000", handler.LastBody);

        // Unset: absent, so the project's mloop.yaml decides — as an absent CLI option does.
        await client.TrainAsync(new MLoopTrainRequest("d.csv", "y", "binary-classification"));
        foreach (var field in new[] { "balance", "timeLimitSeconds", "metric", "testSplit", "maxRows" })
            Assert.DoesNotContain($"\"{field}\"", handler.LastBody);
    }

    [Fact]
    public async Task GetJob_ReadsWhetherMLoopPromoted_WhyItKeptTheModel_AndWhyAJobFailed()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("job-1")
            ? Json(HttpStatusCode.OK, """
                { "jobId": "job-1", "status": "Completed", "experimentId": "exp-4", "promoted": false,
                  "keptInStagingReason": "promote: false was requested" }
                """)
            : Json(HttpStatusCode.OK, """{ "jobId": "job-2", "status": "Failed", "statusMessage": "Label column 'y' not found" }"""));
        var client = Client(handler);

        var kept = await client.GetJobAsync("job-1");
        var failed = await client.GetJobAsync("job-2");

        Assert.Equal(false, kept!.Promoted);
        Assert.Equal("promote: false was requested", kept.KeptInStagingReason);
        Assert.Null(failed!.Promoted);                                      // not reported: unknown, not «no»
        Assert.Equal("Label column 'y' not found", failed.StatusMessage);
    }

    [Fact]
    public async Task RecordFeedback_UnknownPrediction_IsAClientError()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{ "title": "Prediction not found" }"""));

        var error = await Assert.ThrowsAsync<MLoopClientException>(
            () => Client(handler).RecordFeedbackAsync(new MLoopFeedback("0000000000000000", "NG")));
        Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task Predict_FallsBackToScoreWhenNoProbabilities()
    {
        // Regression with no conformal band (old model / bare point estimate) → the scalar score itself.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "task": "regression", "predictions": [ { "score": 0.4 } ] }
            """));

        var pred = await Client(handler).PredictAsync(
            new MLoopPredictionRequest(new Dictionary<string, object?>()));

        Assert.Equal(0.4, pred.Provenance.Confidence!.Value, precision: 6);
    }

    [Fact]
    public async Task Predict_PrefersServerConfidence_OverLocalDerivation()
    {
        // MLoop owns the confidence rule (ConfidencePolicy) and sends it as a `confidence` field. When
        // present it wins, even if the raw probabilities would derive a different value locally.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "task": "binary-classification", "predictions": [
                { "predictedLabel": "NG", "confidence": 0.42, "probabilities": { "NG": 0.95, "OK": 0.05 } } ] }
            """));

        var pred = await Client(handler).PredictAsync(
            new MLoopPredictionRequest(new Dictionary<string, object?>()));

        Assert.Equal(0.42, pred.Provenance.Confidence!.Value, precision: 6); // server value, not max-prob 0.95
    }

    [Theory]
    // D17 — regression confidence from the conformal band width, not the raw Score. confidence =
    // 1 − min(halfWidth / |Score|, 1): a narrow band (certain) is high-confidence, a wide band (the
    // heteroscedastic σ-model flags an uncertain row) low. Before the fix ConfidenceOf clamped the raw
    // Score (a predicted target value, e.g. 15) to [0,1] → meaningless confidence 1.0 for every row.
    [InlineData(15.0, 14.0, 16.0, 0.9333)]  // narrow band (half=1)  → trusted
    [InlineData(15.0, 12.0, 18.0, 0.8000)]  // moderate band (half=3)
    [InlineData(15.0, 8.0, 22.0, 0.5333)]   // wide band (half=7)     → escalate-worthy
    public async Task Predict_RegressionBand_MapsWidthToConfidence(double score, double lower, double upper, double expected)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""
            { "task": "regression", "predictions": [ { "score": {{score.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "scoreLowerBound": {{lower.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "scoreUpperBound": {{upper.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "intervalConfidence": 0.9 } ] }
            """));

        var pred = await Client(handler).PredictAsync(
            new MLoopPredictionRequest(new Dictionary<string, object?>()));

        Assert.Equal(expected, pred.Provenance.Confidence!.Value, precision: 3);
    }

    [Theory]
    // Anomaly confidence is distance from the 0.5 decision boundary, normalized: |score-0.5|*2
    // (RandomizedPca Score ∈ [0,1], threshold 0.5 — Microsoft.ML docs). Confidence is in the
    // anomaly/normal DECISION, not P(anomaly): a strong anomaly AND a strong inlier are both
    // high-confidence; only boundary scores are low-confidence. D10 — before the fix ConfidenceOf
    // read only `probabilities`/`score`, ignored `anomalyScore`, so every anomaly row scored 0.
    [InlineData(0.986, 0.972)]  // strong anomaly  → trusted
    [InlineData(0.197, 0.606)]  // borderline normal
    [InlineData(0.05, 0.90)]    // strong inlier   → trusted (NOT low-confidence)
    [InlineData(0.5, 0.0)]      // exactly on the boundary → no confidence
    [InlineData(1.0, 1.0)]      // most anomalous
    public async Task Predict_AnomalyScore_MapsToBoundaryDistanceConfidence(double anomalyScore, double expected)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, $$"""
            { "task": "anomaly-detection", "predictions": [ { "isAnomaly": true, "anomalyScore": {{anomalyScore.ToString(System.Globalization.CultureInfo.InvariantCulture)}} } ] }
            """));

        var pred = await Client(handler).PredictAsync(
            new MLoopPredictionRequest(new Dictionary<string, object?>()));

        Assert.Equal(expected, pred.Provenance.Confidence!.Value, precision: 3);
    }

    [Fact]
    public async Task Predict_ThrowsMLoopClientExceptionOnServerError()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, """{ "error": "boom" }"""));

        var ex = await Assert.ThrowsAsync<MLoopClientException>(() =>
            Client(handler).PredictAsync(new MLoopPredictionRequest(new Dictionary<string, object?>())));

        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
    }

    [Fact]
    public async Task Train_PostsRequiredFieldsAndReturnsQueuedJob()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Accepted, """
            { "jobId": "job-7", "message": "Training job created." }
            """));

        var job = await Client(handler).TrainAsync(
            new MLoopTrainRequest("data.csv", Label: "target", Task: "binary", Model: "weld"));

        Assert.Equal("job-7", job.Id);
        Assert.Equal("queued", job.Status);
        Assert.Contains("\"dataFile\":\"data.csv\"", handler.LastBody);
        Assert.Contains("\"labelColumn\":\"target\"", handler.LastBody);
        Assert.Contains("\"task\":\"binary\"", handler.LastBody);
    }

    [Fact]
    public async Task Promote_PostsExperimentAndModel()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """{ "promoted": true }"""));

        await Client(handler).PromoteAsync("weld", "exp-003");

        Assert.Contains("promote", handler.LastUri);
        Assert.Contains("\"experimentId\":\"exp-003\"", handler.LastBody);
        Assert.Contains("\"name\":\"weld\"", handler.LastBody);
    }

    [Fact]
    public async Task GetInfo_ParsesTaskAndMetricsMap()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "name": "weld", "task": "binary", "metrics": { "auc": 0.93, "f1": 0.88 } }
            """));

        var info = await Client(handler).GetInfoAsync("weld");

        Assert.NotNull(info);
        Assert.Equal("weld", info!.Model);
        Assert.Equal("binary", info.Task);
        Assert.Equal(0.93, info.Metrics!["auc"]);
        Assert.Equal(0.88, info.Metrics["f1"]);
    }

    [Fact]
    public async Task GetInfo_ReturnsNullOnNotFound()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{ "error": "no model" }"""));

        Assert.Null(await Client(handler).GetInfoAsync("ghost"));
    }

    [Fact]
    public async Task GetJob_CompletedJob_CarriesExperimentAndMetrics()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "jobId": "job-7",
              "status": "Completed",
              "experimentId": "exp-003",
              "metrics": { "auc": 0.94 },
              "bestTrainer": "LightGbm"
            }
            """));

        var job = await Client(handler).GetJobAsync("job-7");

        Assert.NotNull(job);
        Assert.Equal("job-7", job!.Id);
        Assert.Equal("Completed", job.Status);
        Assert.Equal("exp-003", job.ExperimentId);
        Assert.Equal(0.94, job.Metrics!["auc"]);
        Assert.Contains("jobs/job-7", handler.LastUri);
    }

    [Fact]
    public async Task GetJob_RunningJob_HasNoExperimentYet()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "jobId": "job-7", "status": "Running" }
            """));

        var job = await Client(handler).GetJobAsync("job-7");

        Assert.Equal("Running", job!.Status);
        Assert.Null(job.ExperimentId);
        Assert.Null(job.Metrics);
    }

    [Fact]
    public async Task GetJob_ReturnsNullOnNotFound()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.NotFound, """{ "error": "no job" }"""));

        Assert.Null(await Client(handler).GetJobAsync("ghost"));
    }

    [Fact]
    public async Task Forecast_MapsPointsWithBandsAndSendsHorizonObjectBody()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {
              "task": "forecasting",
              "count": 3,
              "predictions": [
                { "score": 10.0, "scoreLowerBound": 9.0,  "scoreUpperBound": 11.0, "intervalConfidence": 0.95, "confidence": 0.9 },
                { "score": 10.5, "scoreLowerBound": 9.0,  "scoreUpperBound": 12.0, "intervalConfidence": 0.95, "confidence": 0.8 },
                { "score": 11.0, "scoreLowerBound": 8.5,  "scoreUpperBound": 13.5, "intervalConfidence": 0.95, "confidence": 0.7 }
              ]
            }
            """));
        var client = Client(handler);

        var forecast = await client.ForecastAsync(new MLoopForecastRequest(Horizon: 3, Model: "demand"));

        Assert.Equal(3, forecast.Value.Points.Count);
        Assert.Equal(10.0, forecast.Value.Points[0].Value);
        Assert.Equal(9.0, forecast.Value.Points[0].LowerBound);
        Assert.Equal(13.5, forecast.Value.Points[2].UpperBound);
        Assert.Equal(0.95, forecast.Value.Points[1].IntervalConfidence);

        Assert.Equal(JudgeKind.Ml, forecast.Provenance.Kind);
        // A forecast's confidence is its weakest step's.
        Assert.Equal(0.7, forecast.Provenance.Confidence!.Value, precision: 6);
        Assert.Equal("mloop:forecasting", forecast.Provenance.Rationale);

        // Request shape: a JSON *object* {"horizon":3} — not the row-array every other task posts.
        Assert.Contains("predict?name=demand", handler.LastUri);
        Assert.Contains("\"horizon\":3", handler.LastBody);
        Assert.StartsWith("{", handler.LastBody!.TrimStart());
    }

    [Fact]
    public async Task Forecast_NullHorizon_SendsEmptyObjectBody()
    {
        // {} = "use the model's trained horizon"; /predict always requires a JSON body.
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "task": "forecasting", "count": 1, "predictions": [ { "score": 5.0 } ] }
            """));

        var forecast = await Client(handler).ForecastAsync(new MLoopForecastRequest());

        Assert.Single(forecast.Value.Points);
        Assert.Equal(5.0, forecast.Value.Points[0].Value);
        Assert.Null(forecast.Value.Points[0].LowerBound);
        Assert.Equal("{}", handler.LastBody!.Trim());
    }

    [Fact]
    public async Task Forecast_MismatchedHorizon_SurfacesServerError()
    {
        // MLoop fails fast with an actionable 400 naming the trained horizon — that must
        // propagate, not be swallowed into an empty forecast.
        var handler = new StubHandler(_ => Json(HttpStatusCode.BadRequest,
            """{ "error": "this forecasting model was trained with a fixed horizon of 5" }"""));

        var ex = await Assert.ThrowsAsync<MLoopClientException>(
            () => Client(handler).ForecastAsync(new MLoopForecastRequest(Horizon: 99)));
        Assert.Contains("fixed horizon of 5", ex.Message);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastUri = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
