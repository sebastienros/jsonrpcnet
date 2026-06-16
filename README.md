![CurlyRpc](https://raw.githubusercontent.com/sebastienros/curlyrpc/main/assets/banner.png)

# CurlyRpc

A minimal-dependency, AOT-friendly **JSON-RPC 2.0** library for .NET (`net8.0` and `net10.0`).

CurlyRpc gives each side of a connection a full-duplex JSON-RPC 2.0 peer with a small, predictable surface:

- **Full-duplex peer** — each side can both invoke remote methods and serve local handlers over a single connection.
- **Standards-compliant JSON-RPC 2.0** with cross-language interoperability (including request batches).
- **Pluggable framing** — `Content-Length` header-delimited (LSP-style) by default, plus newline-delimited.
- **System.Text.Json** serialization with caller-supplied `JsonSerializerOptions`.
- **AOT compatible** with an optional source generator for typed client proxies.
- **Observability** — built-in `ActivitySource` (OpenTelemetry naming) and `Meter`, with an optional `ILogger` bridge.
- **Streaming** results via `IAsyncEnumerable<T>`.
- **Cooperative cancellation** propagated to the remote peer.
- **Key-based authentication** helpers (handshake token validation + transport hardening).
- **Minimal dependencies** — a single AOT- and trim-safe runtime dependency (`Microsoft.Extensions.Logging.Abstractions`).

> Status: under active development.

## Contents

- [Package](#package)
- [Quick start](#quick-start)
- [Connection lifecycle](#connection-lifecycle)
- [Registering local handlers](#registering-local-handlers)
- [Calling remote methods](#calling-remote-methods)
- [Passing parameters (positional and named)](#passing-parameters-positional-and-named)
- [Cancellation](#cancellation)
- [Error handling](#error-handling)
- [Streaming with `IAsyncEnumerable<T>`](#streaming-with-iasyncenumerablet)
- [Typed client proxies (source generator)](#typed-client-proxies-source-generator)
- [Custom serialization (and AOT)](#custom-serialization-and-aot)
- [Message framing](#message-framing)
- [Key-based authentication](#key-based-authentication)
- [Custom inbound middleware](#custom-inbound-middleware)
- [Security and hardening](#security-and-hardening)
- [Observability](#observability)
- [Options reference](#options-reference)
- [Sample](#sample)

## Package

Everything ships in a single NuGet package, **`CurlyRpc`**:

| Included | Description |
| --- | --- |
| Core library | The full-duplex peer, framing, streaming, and observability. |
| Client-proxy source generator | Roslyn generator for typed, AOT-safe proxies (ships as an analyzer asset). |
| `Microsoft.Extensions.Logging` bridge | Optional `JsonRpcLoggingBridge` forwarding diagnostics to `ILogger`. |

Its only runtime dependency is `Microsoft.Extensions.Logging.Abstractions` (AOT- and trim-safe, and part
of the ASP.NET Core shared framework). The source generator adds nothing to your runtime closure.

## Quick start

Create a connection over any duplex `Stream` (the default framing is LSP-style `Content-Length`
headers), register local handlers, and invoke remote methods:

```csharp
using CurlyRpc;

// Each peer is symmetric: it can serve handlers and invoke the other side.
await using var rpc = new JsonRpc(stream);
rpc.AddLocalRpcMethod("add", (int a, int b) => a + b);
rpc.StartListening();

int sum = await rpc.InvokeAsync<int>("add", 2, 3); // 5
await rpc.NotifyAsync("log", "a fire-and-forget notification");
```

## Connection lifecycle

A `JsonRpc` is symmetric and long-lived. Register every handler **before** you start listening, then
either call `StartListening()` or use the `Attach` shortcut that constructs and starts in one step:

```csharp
// Equivalent to: new JsonRpc(stream); rpc.AddLocalRpcMethod(...); rpc.StartListening();
await using var rpc = JsonRpc.Attach(stream);
```

`Completion` is a task that signals when the read loop stops — it completes successfully on
end-of-stream or disposal, and faults if the connection drops because of an error:

```csharp
await using var rpc = JsonRpc.Attach(stream);
await rpc.Completion; // observe disconnection / surface transport errors
```

Disposing the connection (`DisposeAsync`) stops listening, fails any in-flight calls with
`ConnectionLostException`, and — unless you opt out via `JsonRpcOptions.DisposeHandlerOnDispose` —
disposes the underlying message handler so the transport is torn down.

### Stream ownership

When you hand a raw stream to `new JsonRpc(stream)` or `JsonRpc.Attach(stream)`, the connection
**takes ownership of that stream by default** and disposes it when the connection is disposed —
closing the transport so the remote peer observes end-of-stream. This matches StreamJsonRpc and the
BCL stream wrappers (`StreamReader`, `GZipStream`, `SslStream`, …). Set `JsonRpcOptions.OwnsStream =
false` to retain ownership and dispose the stream yourself:

```csharp
await using var rpc = new JsonRpc(stream, new JsonRpcOptions { OwnsStream = false });
// 'stream' is still open here; you dispose it when you're done.
```

Stream ownership only applies when the connection creates its own handler. When you build a handler
explicitly (`new HeaderDelimitedMessageHandler(stream)`), the **handler's** `ownsStream(s)` constructor
argument governs disposal (default `false`, i.e. caller-owns) and `OwnsStream` is ignored.

## Registering local handlers

Register a single delegate, or expose an entire object at once:

```csharp
rpc.AddLocalRpcMethod("add", (int a, int b) => a + b);
```

```csharp
public sealed class Calculator
{
    public int Add(int a, int b) => a + b;

    [JsonRpcMethod("multiply")]            // override the wire name
    public int Multiply(int a, int b) => a * b;

    [JsonRpcIgnore]                        // never exposed
    public int Secret() => 42;
}

rpc.AddLocalRpcTarget(new Calculator());
```

`AddLocalRpcTarget` accepts `JsonRpcTargetOptions` to transform method names (for example, to
camelCase) or to stop walking base types:

```csharp
rpc.AddLocalRpcTarget(new Calculator(), new JsonRpcTargetOptions
{
    MethodNameTransform = name => char.ToLowerInvariant(name[0]) + name[1..],
    IncludeInheritedMethods = false,
});
```

Handlers may be synchronous or asynchronous (`Task`/`Task<T>`/`ValueTask`/`ValueTask<T>`), may take a
trailing `CancellationToken`, and may return `IAsyncEnumerable<T>` to stream results.

By default, a request's `params` array is bound **positionally** to a handler's parameters. JSON-RPC
2.0 also defines **by-name** parameters, where `params` is a JSON object: send one with
`InvokeWithParameterObjectAsync` and each member is matched to the handler parameter of the same name
(order-independent):

```csharp
rpc.AddLocalRpcMethod("subtract", (int minuend, int subtrahend) => minuend - subtrahend);

// Matched by name, regardless of order:
await rpc.InvokeWithParameterObjectAsync<int>("subtract", new { subtrahend = 23, minuend = 42 }); // 19
```

When a handler takes a **single** parameter and no member matches its name, the whole `params` object
is deserialized into that parameter — convenient for an options/DTO object:

```csharp
public sealed class SearchService
{
    [JsonRpcMethod("search")]
    public Task<Results> SearchAsync(SearchRequest request) => /* ... */;
}

// The entire object becomes the `request` argument:
await rpc.InvokeWithParameterObjectAsync<Results>("search", new { query = "rpc", take = 20 });
```

Only methods that are **public**, instance, and declared on your own types are exposed — methods on
`System.Object` (such as `ToString`/`GetHashCode`) and non-public methods are never invokable.

## Calling remote methods

```csharp
int sum = await rpc.InvokeAsync<int>("add", 2, 3); // result expected
await rpc.InvokeAsync("save", new object?[] { doc }, cancellationToken); // result ignored
await rpc.NotifyAsync("log", "no response expected"); // notification
```

`InvokeAsync<TResult>` returns the deserialized result, the parameterless-result `InvokeAsync`
awaits completion without a value, and `NotifyAsync` sends a notification (a request with no `id`,
so the peer never replies).

## Passing parameters (positional and named)

By default arguments are sent **positionally** (a JSON array). To send a **by-name** parameter object
(a JSON object), use the parameter-object overloads — handy for interop with servers that expect
named parameters:

```csharp
// Positional: params -> [2, 3]
await rpc.InvokeAsync<int>("add", 2, 3);

// Named: params -> { "a": 2, "b": 3 }
await rpc.InvokeWithParameterObjectAsync<int>("add", new { a = 2, b = 3 });
await rpc.NotifyWithParameterObjectAsync("track", new { name = "click", count = 1 });
```

## Cancellation

Pass a `CancellationToken` to an invocation; if it fires before the response arrives, the awaited call
throws `OperationCanceledException` immediately (you do not wait for the slow peer) **and** CurlyRpc
sends a `$/cancelRequest` notification (LSP-compatible) so the remote handler's
`CancellationToken` is signaled too:

```csharp
using var cts = new CancellationTokenSource();
Task<int> call = rpc.InvokeAsync<int>("slow", new object?[] { input }, cts.Token);

// Later, from anywhere — cancel the in-flight call:
cts.Cancel();
```

```csharp
// Server side: accept a CancellationToken and honor it.
rpc.AddLocalRpcMethod("slow", async (int n, CancellationToken ct) =>
{
    await Task.Delay(n, ct);
    return n;
});
```

### Timeouts

There is no separate timeout setting — a timeout is just a cancellation token with a deadline. Use
`CancellationTokenSource(TimeSpan)` (or `CancelAfter`) to bound any call. When it elapses, the local
await is canceled and the peer is asked to stop via `$/cancelRequest`:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    int result = await rpc.InvokeAsync<int>("slow", new object?[] { input }, cts.Token);
}
catch (OperationCanceledException)
{
    // The call did not complete within 5 seconds.
}
```

To apply the same deadline to a per-call token and an external one (for example, a host shutdown
token), link them:

```csharp
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, shutdownToken);
int result = await rpc.InvokeAsync<int>("slow", new object?[] { input }, linked.Token);
```

> Calls made without a token (for example, `InvokeAsync<int>("add", 2, 3)`) wait indefinitely until a
> response arrives, the connection drops (`ConnectionLostException`), or the connection is disposed.
> Supply a timed token per call to bound them.

The cancellation method name is configurable via `JsonRpcOptions.CancellationMethodName`.

## Error handling

When a remote handler throws, the caller observes a `RemoteInvocationException` carrying the JSON-RPC
error code and optional structured `data`:

```csharp
try
{
    await rpc.InvokeAsync<int>("divide", 1, 0);
}
catch (RemoteMethodNotFoundException ex)
{
    // -32601: the method does not exist on the peer
}
catch (RemoteInvocationException ex)
{
    Console.WriteLine($"{ex.ErrorCode}: {ex.Message}");
    MyError? detail = ex.GetErrorData<MyError>(rpc.SerializerOptions);
}
```

To return a specific error code and `data` payload from your handler, throw a `LocalRpcException`:

```csharp
rpc.AddLocalRpcMethod("divide", (int a, int b) =>
    b == 0
        ? throw new LocalRpcException("Cannot divide by zero.", JsonRpcErrorCodes.InvalidParams)
        : a / b);
```

Standard codes are available as constants on `JsonRpcErrorCodes` (`ParseError`, `InvalidRequest`,
`MethodNotFound`, `InvalidParams`, `InternalError`, and the server-error range). Pending calls fail
with `ConnectionLostException` if the connection drops first.

## Streaming with `IAsyncEnumerable<T>`

Return `IAsyncEnumerable<T>` from a handler and consume it lazily on the other side. Elements are
streamed in batches; disposing the consumer early aborts the producer:

```csharp
rpc.AddLocalRpcMethod("count", (int n, CancellationToken ct) => Range(n, ct));

await foreach (int value in client.InvokeAsyncEnumerable<int>("count", new object?[] { 5 }))
{
    Console.WriteLine(value);
}
```

The streaming wire protocol (token / values / finished envelope plus `$/enumerator/next` and
`$/enumerator/abort` control messages) follows the common JSON-RPC streaming convention for
cross-implementation interoperability.

## Typed client proxies (source generator)

Annotate an interface with `[JsonRpcProxy]` (the generator ships inside the `CurlyRpc` package). The
generator emits an AOT-safe `Create<Interface>Proxy` extension method:

```csharp
[JsonRpcProxy]
public interface ICalculator
{
    Task<int> AddAsync(int a, int b, CancellationToken cancellationToken = default);

    [JsonRpcMethod("multiply")]
    Task<int> MultiplyAsync(int a, int b);

    IAsyncEnumerable<int> CountAsync(int count, CancellationToken cancellationToken = default);
}

ICalculator calculator = rpc.CreateICalculatorProxy();
int sum = await calculator.AddAsync(2, 3);
```

Supported return shapes: `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, and `IAsyncEnumerable<T>`.
A trailing `CancellationToken` parameter is forwarded to the call.

## Custom serialization (and AOT)

Supply your own `JsonSerializerOptions`. For Native AOT or trimming, back them with a
`JsonSerializerContext` so no reflection is used on the serialization path:

```csharp
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(MyResult))]
internal partial class MyContext : JsonSerializerContext;

var options = new JsonSerializerOptions { TypeInfoResolver = MyContext.Default };
await using var rpc = new JsonRpc(stream, new JsonRpcOptions { SerializerOptions = options });
```

If you do not supply a resolver, a reflection-based default (`JsonSerializerDefaults.Web` —
camelCase, case-insensitive) is used. It is convenient, but not AOT-safe.

### AOT-safe local methods

`AddLocalRpcTarget(object)` and `AddLocalRpcMethod(string, Delegate)` use reflection and are annotated
`[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`. In a Native AOT or trimmed app (especially with
warnings-as-errors), register each method through a strongly-typed `AddLocalRpcMethod<...>` overload
instead — these are reflection-free and bind/serialize entirely through your `JsonSerializerContext`:

```csharp
rpc.AddLocalRpcMethod("getVersion", () => Task.FromResult("9.0.0"));         // Func<Task<TResult>>
rpc.AddLocalRpcMethod("stop", () => Task.CompletedTask);                     // Func<Task>
rpc.AddLocalRpcMethod<string, Result?>("validate", ValidateAsync);           // Func<T1, Task<TResult>>
```

Overloads cover up to two value parameters, an optional trailing `CancellationToken`, and `Task` or
`Task<TResult>` results. A single-value handler also accepts a by-name params object (`{"input": …}`
binds the member value; any other object binds as a request DTO).

> **Tip — passing method groups:** A *parameterless* handler method group binds to the typed overload
> directly, because the only type argument (`TResult`) is inferred from its return type:
> `rpc.AddLocalRpcMethod("getVersion", target.GetVersionAsync)` ✓. When the handler takes value
> parameters, C# can't infer those type arguments from a method group, so it falls back to the
> reflection `Delegate` overload (and its AOT warnings). Disambiguate with explicit type arguments
> (`rpc.AddLocalRpcMethod<string, Result?>("validate", target.ValidateAsync)`), a lambda, or a delegate
> cast.

## Message framing

A *message handler* delimits individual JSON payloads on the wire. The default is
`HeaderDelimitedMessageHandler` (LSP-style `Content-Length` headers). For protocols that separate
messages by newlines, use `NewlineDelimitedMessageHandler`:

```csharp
var handler = new NewlineDelimitedMessageHandler(stream);
await using var rpc = new JsonRpc(handler);
```

Both handlers accept either a single duplex stream or separate send/receive streams, and an
`ownsStream(s)` flag. Implement `IJsonRpcMessageHandler` to support any other framing or transport.

## Key-based authentication

The built-in `HandshakeAuthenticationMiddleware` implements the token handshake used by hosts such as
`microsoft/aspire`: a peer must call `authenticate(token)` before any other method. The token is
compared in constant time; `ping` is allowed pre-auth; a wrong token closes the connection.

```csharp
await using var server = new JsonRpc(stream, new JsonRpcOptions
{
    InboundMiddleware = new HandshakeAuthenticationMiddleware("shared-secret"),
});

// Client side:
await client.InvokeAsync<bool>("authenticate", "shared-secret");
```

For defense in depth over Unix domain sockets, `JsonRpcTransportSecurity.RestrictToCurrentUser(path)`
applies `0600` permissions.

## Custom inbound middleware

Authentication is just one inbound policy. Derive from `JsonRpcInboundMiddleware` to inspect every
incoming request/notification before dispatch and decide whether to proceed, short-circuit with a
result, or reject (optionally closing the connection):

```csharp
public sealed class RateLimitMiddleware : JsonRpcInboundMiddleware
{
    public override ValueTask<JsonRpcDispatchDecision> OnRequestAsync(
        JsonRpcRequestContext context, CancellationToken cancellationToken)
    {
        if (IsOverLimit(context.Method))
        {
            return new(JsonRpcDispatchDecision.Reject(
                JsonRpcErrorCodes.InternalError, "Rate limit exceeded."));
        }

        return new(JsonRpcDispatchDecision.Proceed);
    }
}
```

`JsonRpcDispatchDecision` exposes `Proceed`, `Respond(result)`, and
`Reject(errorCode, message, errorData, closeConnection)`. The `JsonRpcRequestContext` gives you the
method name, raw parameters, whether it is a notification, and the owning connection.

## Security and hardening

JSON-RPC is transport-agnostic, so confidentiality and integrity are the transport's responsibility:
run untrusted or remote connections over an authenticated, encrypted channel (TLS), and use the
key-based handshake above. For local IPC, scope the endpoint to the current user
(`JsonRpcTransportSecurity.RestrictToCurrentUser(path)` for Unix domain sockets; named-pipe ACLs on
Windows). The notes below cover the safeguards built into the library itself.

### Permissive defaults (opt in to hardening)

**CurlyRpc's defaults are intentionally permissive**: a fresh connection has no message-size cap, no
concurrency cap, and no keep-alive, and it surfaces handler exception messages to the caller. These
defaults assume a *trusted, authenticated* transport — which is the case for the local host/CLI
backchannels that motivated the library. They do **not** make a connection safe to expose to an untrusted
peer on their own.

For a connection that can receive data from an untrusted or unauthenticated peer, opt in to conservative
limits in one call with `JsonRpcOptions.CreateHardened()`, then add your serializer/auth:

```csharp
var options = JsonRpcOptions.CreateHardened(MyContext.Default.Options);
options.InboundMiddleware = new HandshakeAuthenticationMiddleware(secret);
await using var server = new JsonRpc(stream, options);
```

`CreateHardened()` sets `MaximumInboundMessageSize = 4 MiB`, `MaximumConcurrentRequests =
ProcessorCount × 16`, and `ExposeExceptionDetails = false`. Keep-alive
is left disabled because the right interval is transport-specific — set `KeepAliveInterval` yourself if
the transport can silently half-open.

### Deserialization safety (a permanent invariant)

**CurlyRpc never deserializes attacker-controlled type information, and never reconstructs exceptions
by type.** Parameters and results are only ever deserialized into the *declared* types of your methods
and proxies (via `System.Text.Json` with your `TypeInfoResolver`); an error response's `data` member is
surfaced as a raw `JsonElement` and only materialized into a type **you** choose
(`RemoteInvocationException.GetErrorData<T>()`). There is no `BinaryFormatter`, no Newtonsoft
`TypeNameHandling`, and no `ISerializable` exception round-tripping. This structurally avoids the
remote-code-execution deserialization class associated with type-name-directed or `ISerializable`-based
exception reconstruction. Deeply nested JSON is bounded by
`System.Text.Json`'s default `MaxDepth` of 64.

### Denial-of-service limits

| Risk | Safeguard | Option (default) |
| --- | --- | --- |
| Memory exhaustion from a huge declared/streamed frame | Oversized frames are rejected before the body is buffered, faulting the connection with `JsonRpcMessageTooLargeException` | `MaximumInboundMessageSize` (`0` = unlimited) |
| Handler/thread-pool exhaustion from a request flood | Concurrent inbound dispatch is capped; excess calls queue | `MaximumConcurrentRequests` (`0` = unlimited) |
| A silently dropped/half-open transport leaving calls hung forever | Periodic `$/ping`; the connection faults with `ConnectionLostException` if the peer does not answer in time | `KeepAliveInterval` / `KeepAliveTimeout` (disabled) |

Set a finite `MaximumInboundMessageSize` (and usually `MaximumConcurrentRequests`) for any connection
exposed to an untrusted peer — or just call `JsonRpcOptions.CreateHardened()`. They default to unlimited
so existing trusted transports are unaffected.
When you supply your own `IJsonRpcMessageHandler`, set `StreamMessageHandler.MaximumMessageSize` (or the
handler constructor parameter) directly — the option is only auto-applied to the handler created by the
`JsonRpc(Stream, …)` constructor.

```csharp
await using var server = new JsonRpc(stream, new JsonRpcOptions
{
    MaximumInboundMessageSize = 1024 * 1024, // reject frames larger than 1 MiB
    MaximumConcurrentRequests = 64,          // at most 64 handlers running at once
    KeepAliveInterval = TimeSpan.FromSeconds(15),
    ExposeExceptionDetails = false,          // see below
});
```

### Avoiding information disclosure

An unhandled exception thrown by a handler is reported to the caller with its `Message` by default
(`ExposeExceptionDetails = true`). For connections facing untrusted peers, set
`ExposeExceptionDetails = false` so unexpected failures return a generic *"An internal error occurred."*
instead of leaking internal detail (paths, SQL, secrets) through exception text. Messages from a
deliberately thrown `LocalRpcException` (and invalid-parameter errors) are author-chosen and are always
sent verbatim.

### Limiting the exposed surface

`AddLocalRpcTarget` makes **every** eligible public method of the target remotely callable. Register a
narrow facade rather than a broad domain object, and apply `[JsonRpcIgnore]` to anything that must not be
reachable. Combine with `InboundMiddleware` for authentication.

## Observability

The core emits a `System.Diagnostics.ActivitySource` and `Meter` named `CurlyRpc`
(`JsonRpcDiagnostics.SourceName`) with OpenTelemetry-style attributes (`rpc.system`, `rpc.method`,
`rpc.jsonrpc.request_id`) and metrics (`rpc.client.duration`, `rpc.server.duration`,
`rpc.client.in_flight`, `rpc.server.in_flight`). Wire it into OpenTelemetry by subscribing to that
source/meter — no dependency required:

```csharp
using var tracer = Sdk.CreateTracerProviderBuilder()
    .AddSource(JsonRpcDiagnostics.SourceName)
    .Build();
```

Or bridge the diagnostics to `Microsoft.Extensions.Logging` (the bridge is built into the package):

```csharp
using CurlyRpc.Extensions.Logging;

using var bridge = JsonRpcLoggingBridge.Create(loggerFactory);
```

### Cross-process trace correlation

By default each peer's spans live in their own trace: the client `rpc.client` span and the server
`rpc.server` span are not linked across the connection. Set `PropagateTraceContext = true` on **both**
peers to carry the active [W3C trace context](https://www.w3.org/TR/trace-context/) (`traceparent`
and `tracestate`) on every outbound request and notification, so the server span parents to the
caller's span and the whole call shows up as a single distributed trace:

```csharp
var options = new JsonRpcOptions { PropagateTraceContext = true };
```

The fields are written as optional `traceparent`/`tracestate` members on the JSON-RPC envelope and are
omitted when there is no active W3C-format `Activity`, so the wire stays backward/forward compatible —
peers that don't understand them (including a peer that leaves the option off) simply ignore the extra
members. Internal control messages (`$/cancelRequest`, `$/ping`, streaming `$/enumerator/*`) never
carry trace context.

## Options reference

`JsonRpcOptions` controls a connection:

| Option | Default | Purpose |
| --- | --- | --- |
| `SerializerOptions` | reflection-based `Web` defaults | `JsonSerializerOptions` for params/results. Back with a `JsonSerializerContext` for AOT. |
| `CancellationMethodName` | `$/cancelRequest` | Method used to signal cancellation to the peer. |
| `DisposeHandlerOnDispose` | `true` | Whether disposing the connection disposes the message handler (transport). |
| `OwnsStream` | `true` | For the `JsonRpc(Stream)` / `Attach(Stream)` entry points: whether disposing the connection disposes the stream it was given (closing the transport). Ignored when you supply your own handler. |
| `InboundMiddleware` | `null` | Hook invoked for every inbound request/notification (auth, rate limiting, …). |
| `PropagateTraceContext` | `false` | When `true`, injects/honors W3C `traceparent`/`tracestate` on requests and notifications so server spans link to the caller's trace. Enable on both peers. |
| `MaximumInboundMessageSize` | `0` (unlimited) | Maximum size in bytes of an inbound frame; larger frames fault the connection with `JsonRpcMessageTooLargeException`. |
| `MaximumConcurrentRequests` | `0` (unlimited) | Maximum number of inbound handlers dispatched concurrently; excess requests queue. |
| `ExposeExceptionDetails` | `true` | When `false`, unhandled handler exceptions return a generic message instead of `Exception.Message`. |
| `KeepAliveInterval` | `TimeSpan.Zero` (disabled) | Interval between automatic `$/ping` liveness probes. |
| `KeepAliveTimeout` | `KeepAliveInterval` | How long to wait for a ping response before faulting with `ConnectionLostException`. |

## Sample

A runnable end-to-end sample (TCP loopback, authentication, a generated proxy, and streaming) lives in
[`samples/CurlyRpc.Sample`](samples/CurlyRpc.Sample):

```sh
dotnet run --project samples/CurlyRpc.Sample
```

## License

MIT
