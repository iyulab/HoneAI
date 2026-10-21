using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HoneAI.Trust;
using Xunit;

namespace HoneAI.Tests;

public class DriftSignalTests
{
    // The shape of MLoop's GET /drift (MLoop.API Program.cs, DriftEvaluator.Signal).
    private const string Measured = """
        {
          "modelName": "risk", "status": "Measured", "experimentId": "exp-004",
          "message": "Significant drift over 400 predictions of exp-004: ...",
          "drift": {
            "rows": 400, "level": "Significant", "maxPsi": 18.4,
            "features": [
              { "column": "Content (unseen words)", "psi": 18.4, "level": "Significant", "mostChangedBin": "≤ 0", "referenceShare": 1, "currentShare": 0 },
              { "column": "Content (length)", "psi": 0.06, "level": "None", "mostChangedBin": "(42, 43]", "referenceShare": 0.1, "currentShare": 0.06 }
            ],
            "prediction": { "column": "(prediction)", "psi": 0.05, "level": "None", "mostChangedBin": "0", "referenceShare": 0.63, "currentShare": 0.52 }
          },
          "sliceBy": "site",
          "slices": [
            { "value": "plant-a", "drift": { "rows": 200, "level": "None", "maxPsi": 0.02, "features": [], "prediction": null } },
            { "value": "plant-b", "drift": { "rows": 200, "level": "Moderate", "maxPsi": 0.2, "features": [], "prediction": { "column": "(prediction)", "psi": 0.3, "level": "Significant" } } }
          ]
        }
        """;

    private static async Task<(MLoopDrift Drift, string Uri)> Read(string json, MLoopDriftRequest request)
    {
        var handler = new Handler(json);
        var client = new HttpMLoopClient(new HttpClient(handler) { BaseAddress = new Uri("http://mloop.local/") });
        return (await client.GetDriftAsync(request), handler.Uri!);
    }

    [Fact]
    public async Task TheDriftEndpoint_IsReadIntoTheContract()
    {
        var (drift, uri) = await Read(Measured, new MLoopDriftRequest("risk", 400, "site"));

        Assert.EndsWith("drift?name=risk&rows=400&sliceBy=site", uri);
        Assert.True(drift.Measured);
        Assert.Equal("exp-004", drift.ExperimentId);
        Assert.Equal(MLoopDriftLevel.Significant, drift.Report!.Level);
        Assert.Equal("Content (unseen words)", drift.Report.Features[0].Column);
        Assert.Equal(MLoopDriftLevel.None, drift.Report.Prediction!.Level);
        Assert.Equal(MLoopDriftLevel.Moderate, drift.Slices!["plant-b"].Level);
    }

    [Fact]
    public async Task Staleness_ReadsTheSlice_WhenMLoopReportedIt()
    {
        var (drift, _) = await Read(Measured, new MLoopDriftRequest());

        Assert.True(StalenessWatch.Drifted(drift));                              // overall: inputs significant
        Assert.False(StalenessWatch.Drifted(drift, sliceValue: "plant-a"));
        Assert.True(StalenessWatch.Drifted(drift, sliceValue: "plant-b"));       // the answers moved there
        Assert.True(StalenessWatch.Drifted(drift, sliceValue: "unknown"));       // no slice report: overall
    }

    [Fact]
    public async Task AWindowMLoopCouldNotMeasure_IsNotDrift()
    {
        var (drift, _) = await Read("""{ "status": "TooFewRows", "experimentId": "exp-004", "message": "12 logged predictions", "drift": null }""", new MLoopDriftRequest());

        Assert.False(drift.Measured);
        Assert.False(StalenessWatch.Drifted(drift, MLoopDriftLevel.None));
    }

    [Fact]
    public async Task AnUnknownLevel_ReadsAsMoved()
    {
        var (drift, _) = await Read("""{ "status": "Measured", "message": "", "drift": { "rows": 200, "level": "Severe", "features": [] } }""", new MLoopDriftRequest());

        Assert.Equal(MLoopDriftLevel.Significant, drift.Report!.Level);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        public string? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
