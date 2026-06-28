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
        Assert.Equal(ReasoningLayer.AutoMl, pred.Provenance.SourceLayer);
        Assert.Equal(0.82, pred.Provenance.Confidence, precision: 6);
        Assert.Equal("mloop:binary", pred.Provenance.Rationale);

        // Request shape: POST predict?name=weld with the single feature row as an array.
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("predict?name=weld", handler.LastUri);
        Assert.Contains("\"temp\":36.5", handler.LastBody);
        Assert.StartsWith("[", handler.LastBody!.TrimStart());
    }

    [Fact]
    public async Task Predict_FallsBackToScoreWhenNoProbabilities()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            { "task": "regression", "predictions": [ { "score": 0.4 } ] }
            """));

        var pred = await Client(handler).PredictAsync(
            new MLoopPredictionRequest(new Dictionary<string, object?>()));

        Assert.Equal(0.4, pred.Provenance.Confidence, precision: 6);
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
