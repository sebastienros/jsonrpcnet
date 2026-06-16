using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CurlyRpc.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void Toolchain_IsWired()
    {
        int methodNotFound = JsonRpcErrorCodes.MethodNotFound;
        Assert.AreEqual(-32601, methodNotFound);
    }
}
