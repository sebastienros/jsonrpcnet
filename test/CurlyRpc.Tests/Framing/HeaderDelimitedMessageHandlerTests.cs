using System.Text;
using CurlyRpc.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests.Framing;

[TestClass]
public sealed class HeaderDelimitedMessageHandlerTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [TestMethod]
    public async Task WritesContentLengthFraming()
    {
        using var output = new MemoryStream();
        var handler = new HeaderDelimitedMessageHandler(output, Stream.Null);

        await handler.WriteMessageAsync(Utf8("{\"a\":1}"), CancellationToken.None);

        string framed = Encoding.UTF8.GetString(output.ToArray());
        Assert.AreEqual("Content-Length: 7\r\n\r\n{\"a\":1}", framed);
    }

    [TestMethod]
    public async Task RoundTripsMultipleMessages()
    {
        byte[] wire = await FrameAsync("{\"first\":true}", "{\"second\":42}");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("{\"first\":true}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"second\":42}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(7)]
    [DataRow(16)]
    public async Task ParsesAcrossArbitraryChunkBoundaries(int chunkSize)
    {
        byte[] wire = await FrameAsync("{\"msg\":\"hello world\"}", "{\"n\":2}");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, chunkSize));

        Assert.AreEqual("{\"msg\":\"hello world\"}", await ReadStringAsync(handler));
        Assert.AreEqual("{\"n\":2}", await ReadStringAsync(handler));
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task ToleratesExtraHeadersAndCaseInsensitiveName()
    {
        byte[] wire = Utf8("Content-Type: application/json\r\ncontent-length: 5\r\n\r\n[1,2]");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        Assert.AreEqual("[1,2]", await ReadStringAsync(handler));
    }

    [TestMethod]
    public async Task EmptyStreamReturnsNull()
    {
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream());
        Assert.IsNull(await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task TruncatedBodyThrows()
    {
        byte[] wire = Utf8("Content-Length: 50\r\n\r\n{\"partial\":");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task MissingContentLengthThrows()
    {
        byte[] wire = Utf8("X-Whatever: 1\r\n\r\n{}");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task HandlesLargeMessageRequiringBufferGrowth()
    {
        string payload = "{\"data\":\"" + new string('x', 100_000) + "\"}";
        byte[] wire = await FrameAsync(payload);
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new ChunkedReadStream(wire, 333));

        Assert.AreEqual(payload, await ReadStringAsync(handler));
    }

    [TestMethod]
    public async Task OversizedContentLength_ThrowsMessageTooLarge()
    {
        byte[] wire = Utf8("Content-Length: 5000\r\n\r\n");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire), maximumMessageSize: 16);

        var ex = await Assert.ThrowsExactlyAsync<JsonRpcMessageTooLargeException>(
            async () => await handler.ReadMessageAsync(CancellationToken.None));
        Assert.AreEqual(16, ex.MaximumMessageSize);
    }

    [TestMethod]
    public async Task WithinSizeLimit_ReadsNormally()
    {
        byte[] wire = await FrameAsync("[1,2]");
        var handler = new HeaderDelimitedMessageHandler(Stream.Null, new MemoryStream(wire), maximumMessageSize: 1024);

        Assert.AreEqual("[1,2]", await ReadStringAsync(handler));
    }

    private static async Task<byte[]> FrameAsync(params string[] messages)
    {
        using var output = new MemoryStream();
        var handler = new HeaderDelimitedMessageHandler(output, Stream.Null);
        foreach (string message in messages)
        {
            await handler.WriteMessageAsync(Utf8(message), CancellationToken.None);
        }

        return output.ToArray();
    }

    private static async Task<string> ReadStringAsync(IJsonRpcMessageHandler handler)
    {
        ReadOnlyMemory<byte>? message = await handler.ReadMessageAsync(CancellationToken.None);
        Assert.IsNotNull(message);
        return Encoding.UTF8.GetString(message.Value.Span);
    }
}
