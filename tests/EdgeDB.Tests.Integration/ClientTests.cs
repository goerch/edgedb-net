using EdgeDB.DataTypes;
using EdgeDB.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EdgeDB.Tests.Integration
{
    [TestClass]
    public class ClientTests
    {
        internal EdgeDBClient EdgeDB { get; set; }
        private readonly Func<CancellationToken> _getToken;
        
        public ClientTests()
        {
            EdgeDB = ClientProvider.EdgeDB;
            _getToken = () => ClientProvider.GetTimeoutToken();
        }

        [TestMethod]
        public async Task TestCommandLocks()
        {
            await using var client = await EdgeDB.GetOrCreateClientAsync<EdgeDBBinaryClient>(token: _getToken());
            var timeoutToken = new CancellationTokenSource();
            timeoutToken.CancelAfter(1000);
            using var firstLock = await client.AquireCommandLockAsync(timeoutToken.Token);

            await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () =>
            {
                var secondLock = await client.AquireCommandLockAsync(timeoutToken.Token);
            });
        }

        [TestMethod]
        public async Task TestPoolQueryMethods()
        {
            var jsonResult = await EdgeDB.QueryJsonAsync("select {(a := 1), (a := 2)}", token: _getToken());
            Assert.AreEqual("[{\"a\" : 1}, {\"a\" : 2}]", jsonResult.Value);

            var queryJsonElementsResult = await EdgeDB.QueryJsonElementsAsync("select {(a := 1), (a := 2)}", token: _getToken());

            Assert.AreEqual(2, queryJsonElementsResult.Count());

            Assert.AreEqual("{\"a\" : 1}", queryJsonElementsResult.First().Value);
            Assert.AreEqual("{\"a\" : 2}", queryJsonElementsResult.Last().Value);

            var querySingleResult = await EdgeDB.QuerySingleAsync<long>("select 123", token: _getToken()).ConfigureAwait(false);
            Assert.AreEqual(123, querySingleResult);

            var queryRequiredSingeResult = await EdgeDB.QueryRequiredSingleAsync<long>("select 123", token: _getToken());
            Assert.AreEqual(123, queryRequiredSingeResult);
        }

        [TestMethod]
        public async Task TestPoolRelease()
        {
            BaseEdgeDBClient client;
            await using (client = await EdgeDB.GetOrCreateClientAsync(token: _getToken()))
            {
                await Task.Delay(100);
            }

            // client should be back in the pool
            Assert.IsTrue(EdgeDB.Clients.Contains(client));
        }

        [TestMethod]
        public virtual async Task TestPoolTransactions()
        {
            var result = await EdgeDB.TransactionAsync(async (tx) =>
            {
                return await tx.QuerySingleAsync<string>("select \"Transaction within pools\"", token: _getToken());
            });

            Assert.AreEqual("Transaction within pools", result);
        }

        [TestMethod]
        public async Task DisconnectAndReconnect()
        {
            // using raw client for this one,
            var client = await EdgeDB.GetOrCreateClientAsync(token: _getToken());

            // disconnect should close the underlying connection, and remove alloc'd resources for said connection.
            await client.DisconnectAsync(token: _getToken());

            // should run just fine, restarting the underlying connection.
            var str = await client.QueryRequiredSingleAsync<string>("select \"Hello, EdgeDB.Net!\"", token: _getToken());

            Assert.AreEqual("Hello, EdgeDB.Net!", str);
        }

        [TestMethod]
        public void StateChange()
        {
            var client2 = EdgeDB.WithConfig(x => x.DDLPolicy = DDLPolicy.AlwaysAllow);

            Assert.IsNull(EdgeDB.Config.DDLPolicy);
            Assert.AreEqual(DDLPolicy.AlwaysAllow, client2.Config.DDLPolicy);

            var client3 = client2.WithModule("test_module");

            Assert.IsNull(EdgeDB.Config.DDLPolicy);
            Assert.AreEqual(DDLPolicy.AlwaysAllow, client2.Config.DDLPolicy);
            Assert.AreEqual("test_module", client3.Module);
            Assert.AreNotEqual("test_module", client2.Module);
            Assert.AreNotEqual("test_module", EdgeDB.Module);
        }
    }
}
