using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class JsonRpcTests
{
    private static JsonRpcOptions CreateOptions()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    [TestMethod]
    public async Task InvokeAsync_PositionalArguments_ReturnsResult()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        server.AddLocalRpcMethod("add", (int a, int b) => a + b);
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("add", 2, 3);

        Assert.AreEqual(5, result);
    }

    [TestMethod]
    public async Task InvokeAsync_AsyncHandler_ReturnsResult()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        server.AddLocalRpcMethod("echo", async (string s) =>
        {
            await Task.Yield();
            return s + s;
        });
        server.StartListening();
        client.StartListening();

        string result = (await client.InvokeAsync<string>("echo", "ab"))!;

        Assert.AreEqual("abab", result);
    }

    [TestMethod]
    public async Task InvokeAsync_UnknownMethod_ThrowsRemoteMethodNotFound()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("missing", 1));

        Assert.AreEqual(JsonRpcErrorCodes.MethodNotFound, ex.ErrorCode);
        Assert.AreEqual("missing", ex.MethodName);
    }

    [TestMethod]
    public async Task InvokeAsync_HandlerThrowsLocalRpcException_PropagatesCodeAndData()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        server.AddLocalRpcMethod("fail", (Func<int>)(() => throw new LocalRpcException("boom", -32001, new ErrorPayload { Detail = "nope" })));
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<int>("fail"));

        Assert.AreEqual(-32001, ex.ErrorCode);
        Assert.AreEqual("boom", ex.Message);
        Assert.IsNotNull(ex.ErrorData);
        Assert.AreEqual("nope", ex.ErrorData!.Value.GetProperty("detail").GetString());
    }

    [TestMethod]
    public async Task NotifyAsync_InvokesHandler_NoResponse()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("notify", (string message) => received.TrySetResult(message));
        server.StartListening();
        client.StartListening();

        await client.NotifyAsync("notify", "hello");

        string got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("hello", got);
    }

    [TestMethod]
    public async Task InvokeWithParameterObjectAsync_BindsSingleObject()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        server.AddLocalRpcTarget(new Calculator());
        server.StartListening();
        client.StartListening();

        var result = await client.InvokeWithParameterObjectAsync<Sum>("Combine", new AddArgs { X = 4, Y = 6 });

        Assert.AreEqual(10, result!.Total);
    }

    [TestMethod]
    public async Task InvokeAsync_CallerCancels_ThrowsAndCancelsServerHandler()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        var serverObservedCancellation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("wait", async (CancellationToken ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                serverObservedCancellation.TrySetResult();
                throw;
            }
        });
        server.StartListening();
        client.StartListening();

        using var cts = new CancellationTokenSource();
        var invocation = client.InvokeAsync<object>("wait", Array.Empty<object?>(), cts.Token);

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await invocation);
        await serverObservedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Bidirectional_ServerCanInvokeClient()
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        await using var client = new JsonRpc(h1, CreateOptions());
        await using var server = new JsonRpc(h2, CreateOptions());

        client.AddLocalRpcMethod("clientPing", () => "pong");
        server.AddLocalRpcMethod("relay", async () => await server.InvokeAsync<string>("clientPing"));

        server.StartListening();
        client.StartListening();

        string result = (await client.InvokeAsync<string>("relay"))!;
        Assert.AreEqual("pong", result);
    }

    private sealed class Calculator
    {
        public Sum Combine(AddArgs args) => new() { Total = args.X + args.Y };
    }

    private sealed class AddArgs
    {
        public int X { get; set; }

        public int Y { get; set; }
    }

    private sealed class Sum
    {
        public int Total { get; set; }
    }

    private sealed class ErrorPayload
    {
        public string Detail { get; set; } = string.Empty;
    }
}
