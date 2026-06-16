using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class SecurityTests
{
    private static JsonSerializerOptions Web() => new(JsonSerializerDefaults.Web);

    // ---- Permissive defaults / CreateHardened ----------------

    [TestMethod]
    public void ConstructorDefaults_AreUnrestricted()
    {
        // CurlyRpc's defaults are intentionally permissive: no caps, exception detail exposed.
        var options = new JsonRpcOptions();
        Assert.AreEqual(0, options.MaximumInboundMessageSize);
        Assert.AreEqual(0, options.MaximumConcurrentRequests);
        Assert.IsTrue(options.ExposeExceptionDetails);
        Assert.AreEqual(TimeSpan.Zero, options.KeepAliveInterval);
    }

    [TestMethod]
    public void CreateHardened_AppliesConservativeLimits()
    {
        var serializer = Web();
        var options = JsonRpcOptions.CreateHardened(serializer);

        Assert.AreSame(serializer, options.SerializerOptions);
        Assert.AreEqual(JsonRpcOptions.DefaultHardenedMaximumInboundMessageSize, options.MaximumInboundMessageSize);
        Assert.AreEqual(4 * 1024 * 1024, options.MaximumInboundMessageSize);
        Assert.AreEqual(Environment.ProcessorCount * 16, options.MaximumConcurrentRequests);
        Assert.IsFalse(options.ExposeExceptionDetails);
    }

    // ---- MaximumInboundMessageSize -----------------------------------------

    [TestMethod]
    public async Task OversizedFrame_FaultsConnection_ViaHandlerCap()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        h2.MaximumMessageSize = 64; // server receive cap
        var client = new JsonRpc(h1, new JsonRpcOptions { SerializerOptions = Web() });
        var server = new JsonRpc(h2, new JsonRpcOptions { SerializerOptions = Web() });
        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();
        await using var _c = client;

        await client.NotifyAsync("echo", new string('x', 500));

        await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task MaximumInboundMessageSizeOption_IsAppliedToDefaultHandler()
    {
        // The option must flow into the handler created by the JsonRpc(Stream) constructor.
        byte[] frame = Encoding.UTF8.GetBytes("Content-Length: 500\r\n\r\n" + new string('x', 500));
        var rpc = new JsonRpc(new MemoryStream(frame), new JsonRpcOptions
        {
            SerializerOptions = Web(),
            MaximumInboundMessageSize = 64,
        });
        rpc.StartListening();

        await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await rpc.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---- MaximumConcurrentRequests -----------------------------------------

    [TestMethod]
    public async Task MaximumConcurrentRequests_BoundsHandlerConcurrency()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, new JsonRpcOptions { SerializerOptions = Web() });
        var server = new JsonRpc(h2, new JsonRpcOptions
        {
            SerializerOptions = Web(),
            MaximumConcurrentRequests = 2,
        });

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object sync = new();
        int current = 0;
        int max = 0;

        server.AddLocalRpcMethod("work", async () =>
        {
            lock (sync)
            {
                current++;
                if (current > max)
                {
                    max = current;
                }
            }

            await gate.Task;
            lock (sync)
            {
                current--;
            }

            return true;
        });

        server.StartListening();
        client.StartListening();
        await using var _c = client;
        await using var _s = server;

        Task<bool>[] calls = Enumerable.Range(0, 4)
            .Select(_ => client.InvokeAsync<bool>("work"))
            .ToArray();

        // Wait for the throttle to saturate, then confirm a third handler never starts.
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref current) < 2 && sw.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        await Task.Delay(200);
        Assert.AreEqual(2, Volatile.Read(ref max), "No more than MaximumConcurrentRequests handlers should run at once.");

        gate.SetResult();
        bool[] results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        CollectionAssert.AreEqual(new[] { true, true, true, true }, results);
    }

    // ---- ExposeExceptionDetails --------------------------------------------

    [TestMethod]
    public async Task ExposeExceptionDetailsFalse_ScrubsMessage()
    {
        var (client, server) = CreateBoomPair(exposeExceptionDetails: false);
        await using var _c = client;
        await using var _s = server;

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("boom"));

        Assert.AreEqual(JsonRpcErrorCodes.InternalError, ex.ErrorCode);
        StringAssert.Contains(ex.Message, "internal error");
        Assert.IsFalse(ex.Message.Contains("secret-detail"), "Internal detail must not leak when scrubbing is enabled.");
    }

    [TestMethod]
    public async Task ExposeExceptionDetailsTrue_IsDefault_AndIncludesMessage()
    {
        var (client, server) = CreateBoomPair(exposeExceptionDetails: true);
        await using var _c = client;
        await using var _s = server;

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<bool>("boom"));

        StringAssert.Contains(ex.Message, "secret-detail");
    }

    // ---- Keep-alive ---------------------------------------------------------

    [TestMethod]
    public async Task KeepAlive_RespondingPeer_KeepsConnectionAlive()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, new JsonRpcOptions
        {
            SerializerOptions = Web(),
            KeepAliveInterval = TimeSpan.FromMilliseconds(100),
            // Generous timeout: a responsive peer must never trip keep-alive, even when a loaded
            // CI runner delays a ping reply. Tripping is covered by the unresponsive-peer test.
            KeepAliveTimeout = TimeSpan.FromSeconds(2),
        });
        var server = new JsonRpc(h2, new JsonRpcOptions { SerializerOptions = Web() });
        server.AddLocalRpcMethod("echo", (string s) => s);
        server.StartListening();
        client.StartListening();
        await using var _c = client;
        await using var _s = server;

        await Task.Delay(450); // several keep-alive intervals

        Assert.IsFalse(client.Completion.IsCompleted, "A responsive peer must not trip keep-alive.");
        Assert.AreEqual("hi", await client.InvokeAsync<string>("echo", "hi"));
    }

    [TestMethod]
    public async Task KeepAlive_UnresponsivePeer_FaultsWithConnectionLost()
    {
        var (h1, _) = DuplexConnection.CreateHandlerPair(); // nothing reads/answers the other end
        var client = new JsonRpc(h1, new JsonRpcOptions
        {
            SerializerOptions = Web(),
            KeepAliveInterval = TimeSpan.FromMilliseconds(150),
            KeepAliveTimeout = TimeSpan.FromMilliseconds(150),
        });
        client.StartListening();
        await using var _c = client;

        await Assert.ThrowsExactlyAsync<ConnectionLostException>(
            async () => await client.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task BuiltInPing_IsAnswered()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, new JsonRpcOptions { SerializerOptions = Web() });
        var server = new JsonRpc(h2, new JsonRpcOptions { SerializerOptions = Web() });
        server.StartListening();
        client.StartListening();
        await using var _c = client;
        await using var _s = server;

        // No handler registered for "$/ping"; the built-in answers with a null result.
        JsonElement? result = await client.InvokeAsync<JsonElement?>("$/ping");
        Assert.IsTrue(result is null || result.Value.ValueKind == JsonValueKind.Null);
    }

    private static (JsonRpc Client, JsonRpc Server) CreateBoomPair(bool exposeExceptionDetails)
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        var client = new JsonRpc(h1, new JsonRpcOptions { SerializerOptions = Web() });
        var server = new JsonRpc(h2, new JsonRpcOptions
        {
            SerializerOptions = Web(),
            ExposeExceptionDetails = exposeExceptionDetails,
        });

        server.AddLocalRpcMethod("boom", async () =>
        {
            await Task.Yield();
            throw new InvalidOperationException("secret-detail");
        });

        server.StartListening();
        client.StartListening();
        return (client, server);
    }
}
