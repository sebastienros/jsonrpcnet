using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class DiagnosticsTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    // The server span/metric is recorded in a finally block that runs *after* the response is written
    // to the wire, so it can race the client's InvokeAsync completion. Poll until the expected signal
    // appears instead of assuming it is present the instant the client call returns. The sinks are also
    // written from the server dispatch thread, so they must be thread-safe.
    private static async Task WaitUntilAsync(Func<bool> condition, string describe, double timeoutSeconds = 5)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(timeoutSeconds))
            {
                Assert.Fail($"{describe} was not observed within {timeoutSeconds}s.");
            }

            await Task.Delay(15);
        }
    }

    [TestMethod]
    public async Task Invoke_EmitsClientAndServerActivities()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JsonRpcDiagnostics.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);

        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("square", (int x) => x * x);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("square", 6);
        Assert.AreEqual(36, result);

        await WaitUntilAsync(
            () => activities.Any(a => a.Kind == ActivityKind.Client && a.DisplayName == "square"),
            "client activity 'square'");
        await WaitUntilAsync(
            () => activities.Any(a => a.Kind == ActivityKind.Server && a.DisplayName == "square"),
            "server activity 'square'");
    }

    [TestMethod]
    public async Task Invoke_RecordsClientDurationMetric()
    {
        var measurements = new ConcurrentBag<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == JsonRpcDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) => measurements.Add(instrument.Name));
        meterListener.Start();

        var (client, server) = CreatePair();
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("ping", () => "pong");
        server.StartListening();
        client.StartListening();

        _ = await client.InvokeAsync<string>("ping");

        // The server-side duration is recorded after the response is written, so it can lag the client's
        // call completion; poll for both before tearing down the listener.
        await WaitUntilAsync(() => measurements.Contains("rpc.client.duration"), "rpc.client.duration measurement");
        await WaitUntilAsync(() => measurements.Contains("rpc.server.duration"), "rpc.server.duration measurement");

        meterListener.Dispose();
    }
}
