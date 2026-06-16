using System.Text.Json;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Integration;

[TestClass]
public sealed class DispatchTests
{
    private static JsonRpcOptions Options()
        => new() { SerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) };

    private static (JsonRpc Client, JsonRpc Server) CreatePair(out HeaderDelimitedMessageHandler clientHandler, out HeaderDelimitedMessageHandler serverHandler)
    {
        var (h1, h2) = DuplexConnection.CreateHandlerPair();
        clientHandler = h1;
        serverHandler = h2;
        return (new JsonRpc(h1, Options()), new JsonRpc(h2, Options()));
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_AppliesMethodNameTransform()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Service(), new JsonRpcTargetOptions
        {
            MethodNameTransform = name => "svc/" + name,
        });
        server.StartListening();
        client.StartListening();

        int result = await client.InvokeAsync<int>("svc/Square", 5);
        Assert.AreEqual(25, result);
    }

    [TestMethod]
    public async Task AddLocalRpcTarget_HonorsExplicitNameAndIgnore()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Service());
        server.StartListening();
        client.StartListening();

        string greeting = (await client.InvokeAsync<string>("greet", "world"))!;
        Assert.AreEqual("hello world", greeting);

        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Secret"));
    }

    [TestMethod]
    public async Task VoidTaskHandler_CompletesInvocation()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.AddLocalRpcMethod("run", async () =>
        {
            await Task.Yield();
            ran.TrySetResult();
        });
        server.StartListening();
        client.StartListening();

        await client.InvokeAsync("run", Array.Empty<object?>(), CancellationToken.None);
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task InvalidParameters_ReturnInvalidParamsError()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("needsInt", (int value) => value);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            async () => await client.InvokeAsync<int>("needsInt", "not-a-number"));

        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [TestMethod]
    public async Task NullResult_DeserializesToDefaultForValueTypes()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("getNull", () => (string?)null);
        server.StartListening();
        client.StartListening();

        // A JSON null result must round-trip gracefully into reference types, nullable value types,
        // and (defensively) non-nullable value types — never throw.
        Assert.IsNull(await client.InvokeAsync<string?>("getNull"));
        Assert.IsNull(await client.InvokeAsync<int?>("getNull"));
        Assert.AreEqual(0, await client.InvokeAsync<int>("getNull"));
    }

    [TestMethod]
    public async Task ObjectAndNonPublicMethods_AreNotInvokable()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new Restricted());
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(1, await client.InvokeAsync<int>("Visible"));

        // Methods declared on System.Object must not be exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("ToString"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("GetHashCode"));
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<string>("GetType"));

        // Non-public methods must not be exposed.
        await Assert.ThrowsExactlyAsync<RemoteMethodNotFoundException>(
            async () => await client.InvokeAsync<int>("Hidden"));
    }

    [TestMethod]
    public async Task Handler_WithOnlyCancellationToken_IsInvokable()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        // A method whose sole parameter is a CancellationToken must bind with no JSON params.
        server.AddLocalRpcMethod("tick", (CancellationToken ct) => 7);
        server.StartListening();
        client.StartListening();

        Assert.AreEqual(7, await client.InvokeAsync<int>("tick"));
    }

    [TestMethod]
    public async Task Handler_WithJsonElementAndCancellationToken_BindsParameterObject()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        // A handler taking (JsonElement, CancellationToken) must receive the whole by-name params
        // object in its JsonElement parameter.
        server.AddLocalRpcMethod(
            "describe",
            (JsonElement data, CancellationToken ct) => data.GetProperty("name").GetString());
        server.StartListening();
        client.StartListening();

        string? name = await client.InvokeWithParameterObjectAsync<string>("describe", new { name = "neo" });
        Assert.AreEqual("neo", name);
    }

    [TestMethod]
    public async Task SingleDtoParameter_BindsWholeParamsObject()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcTarget(new PointService());
        server.StartListening();
        client.StartListening();

        // A handler with a single DTO parameter receives the whole params object when no member
        // matches the parameter name.
        int sum = await client.InvokeWithParameterObjectAsync<int>("addPoint", new { x = 2, y = 3 });
        Assert.AreEqual(5, sum);
    }

    [TestMethod]
    public async Task ByNameParams_BindToMultipleParametersByName()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("subtract", (int minuend, int subtrahend) => minuend - subtrahend);
        server.StartListening();
        client.StartListening();

        // JSON-RPC 2.0 by-name params (spec example): members are matched to parameter names
        // regardless of order.
        int result = await client.InvokeWithParameterObjectAsync<int>(
            "subtract", new { subtrahend = 23, minuend = 42 });
        Assert.AreEqual(19, result);
    }

    [TestMethod]
    public async Task ByNameParams_MissingRequiredParameter_ReportsInvalidParams()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("subtract", (int minuend, int subtrahend) => minuend - subtrahend);
        server.StartListening();
        client.StartListening();

        var ex = await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
            () => client.InvokeWithParameterObjectAsync<int>("subtract", new { minuend = 42 }));
        Assert.AreEqual(JsonRpcErrorCodes.InvalidParams, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ByNameParams_OmittedOptionalParameter_UsesDefault()
    {
        var (client, server) = CreatePair(out _, out _);
        await using var _c = client;
        await using var _s = server;

        server.AddLocalRpcMethod("scale", (int value, int factor) => value * factor);
        server.StartListening();
        client.StartListening();

        int doubled = await client.InvokeWithParameterObjectAsync<int>("scale", new { value = 21, factor = 2 });
        Assert.AreEqual(42, doubled);
    }

    private sealed class Service
    {
        public int Square(int value) => value * value;

        [JsonRpcMethod("greet")]
        public string Greeting(string name) => "hello " + name;

        [JsonRpcIgnore]
        public int Secret() => 42;
    }

    private sealed class Restricted
    {
        public int Visible() => 1;

        private int Hidden() => 2;
    }

    private sealed class PointService
    {
        [JsonRpcMethod("addPoint")]
        public int Add(Point point) => point.X + point.Y;
    }

    public sealed class Point
    {
        public int X { get; set; }

        public int Y { get; set; }
    }
}
