using System.Text;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Framing;

[TestClass]
public sealed class NewlineDelimitedMessageHandlerTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [TestMethod]
    public async Task WritesNewlineFraming()
    {
        using var output = new MemoryStream();
        var handler = new NewlineDelimitedMessageHandler(output, Stream.Null);

        await handler.WriteMessageAsync(Utf8("{\"a\":1}"), CancellationToken.None);

        Assert.AreEqual("{\"a\":1}\n", Encoding.UTF8.GetString(output.ToArray()));
    }

    [TestMethod]
    public async Task RoundTripsMultipleMessages()
    {
        byte[] wire = Utf8("{\"a\":1}\n{\"b\":2}\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task IgnoresBlankLinesAndToleratesCrlf()
    {
        byte[] wire = Utf8("\n{\"a\":1}\r\n\r\n{\"b\":2}\r\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(5)]
    public async Task ParsesAcrossChunkBoundaries(int chunkSize)
    {
        byte[] wire = Utf8("{\"a\":1}\n{\"b\":2}\n");
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, chunkSize));

        Assert.AreEqual("{\"a\":1}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"b\":2}", await ReadStringAsync(handler));
    }

    [TestMethod]
    public async Task UnterminatedBodyOverLimit_ThrowsMessageTooLarge()
    {
        // A long line with no terminating newline must not buffer without bound.
        byte[] wire = Utf8(new string('x', 200));
        var handler = new NewlineDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, 8), maximumMessageSize: 32);

        var ex = await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
        Assert.AreEqual(32, ex.MaximumMessageSize);
    }

    private static async Task<string> ReadStringAsync(IJsonRpcMessageHandler handler)
    {
        ReadOnlyMemory<byte>? message = await handler.ReadMessageAsync(CancellationToken.None);
        Assert.IsNotNull(message);
        return Encoding.UTF8.GetString(message.Value.Span);
    }
}
