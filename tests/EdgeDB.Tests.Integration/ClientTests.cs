using EdgeDB.DataTypes;
using EdgeDB.State;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace EdgeDB.Tests.Integration
{
    public class ClientTests : IClassFixture<ClientFixture>
    {
        private readonly EdgeDBClient _edgedb;
        private readonly ITestOutputHelper _output;
        private readonly Func<CancellationToken> _getToken;
        
        public ClientTests(ClientFixture clientFixture, ITestOutputHelper output)
        {
            _edgedb = clientFixture.EdgeDB;
            _output = output;
            _getToken = () => clientFixture.GetTimeoutToken();
        }

        [Fact]
        public async Task TestCommandLocks()
        {
            await using var client = await _edgedb.GetOrCreateClientAsync<EdgeDBBinaryClient>(token: _getToken());
            var timeoutToken = new CancellationTokenSource();
            timeoutToken.CancelAfter(1000);
            using var firstLock = await client.AquireCommandLockAsync(timeoutToken.Token);

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var secondLock = await client.AquireCommandLockAsync(timeoutToken.Token);
            });
        }

        [Fact]
        public async Task TestPoolQueryMethods()
        {
            var jsonResult = await _edgedb.QueryJsonAsync("select {(a := 1), (a := 2)}", token: _getToken());
            Assert.Equal("[{\"a\" : 1}, {\"a\" : 2}]", jsonResult);

            var queryJsonElementsResult = await _edgedb.QueryJsonElementsAsync("select {(a := 1), (a := 2)}", token: _getToken());

            Assert.Equal(2, queryJsonElementsResult.Count());

            Assert.Equal("{\"a\" : 1}", queryJsonElementsResult.First());
            Assert.Equal("{\"a\" : 2}", queryJsonElementsResult.Last());

            var querySingleResult = await _edgedb.QuerySingleAsync<long>("select 123", token: _getToken()).ConfigureAwait(false);
            Assert.Equal(123, querySingleResult);

            var queryRequiredSingeResult = await _edgedb.QueryRequiredSingleAsync<long>("select 123", token: _getToken());
            Assert.Equal(123, queryRequiredSingeResult);
        }

        [Fact]
        public async Task TestPoolRelease()
        {
            BaseEdgeDBClient client;
            await using (client = await _edgedb.GetOrCreateClientAsync(token: _getToken()))
            {
                await Task.Delay(100);
            }

            // client should be back in the pool
            Assert.Contains(client, _edgedb.Clients);
        }

        [Fact]
        public async Task TestPoolTransactions()
        {
            var result = await _edgedb.TransactionAsync(async (tx) =>
            {
                return await tx.QuerySingleAsync<string>("select \"Transaction within pools\"", token: _getToken());
            });

            Assert.Equal("Transaction within pools", result);
        }

        [Fact]
        public async Task StandardScalarQueries()
        {
            await TestScalarQuery("true", true);
            await TestScalarQuery("b'bina\\x01ry'", Encoding.UTF8.GetBytes("bina\x01ry"));
            await TestScalarQuery("<datetime>'1999-03-31T15:17:00Z'", DateTimeOffset.Parse("1999-03-31T15:17:00Z"));
            await TestScalarQuery("<cal::local_datetime>'1999-03-31T15:17:00'", DateTime.Parse("1999-03-31T15:17:00"));
            await TestScalarQuery<DateOnly>("<cal::local_date>'1999-03-31'", new(1999, 3, 31));
            await TestScalarQuery<TimeSpan>("<cal::local_time>'15:17:00'", new(15,17,0));
            await TestScalarQuery("42.0n", (decimal)42.0);
            await TestScalarQuery("3.14", 3.14f);
            await TestScalarQuery("314e-2", 314e-2);
            await TestScalarQuery<short>("<int16>1234", 1234);
            await TestScalarQuery("<int32>123456", 123456);
            await TestScalarQuery<long>("1234", 1234);
            await TestScalarQuery("\"Hello, Tests!\"", "Hello, Tests!");
        }

        [Fact]
        public async Task ArrayQueries()
        {
            await TestScalarQuery("[1,2,3]", new long[] { 1, 2, 3 });
            await TestScalarQuery("[\"Hello\", \"World\"]", new string[] { "Hello", "World" });
        }

        [Fact]
        public async Task TupleQueries()
        {
            var result = await _edgedb.QuerySingleAsync<(long one, long two)>("select (1,2)", token: _getToken());
            Assert.Equal(1, result.one);
            Assert.Equal(2, result.two);

            var (one, two, three, four, five, six, seven, eight, nine, ten) = await _edgedb.QuerySingleAsync<(long one, long two, long three, long four, long five, long six, long seven, long eight, long nine, long ten)>("select (1,2,3,4,5,6,7,8,9,10)", token: _getToken());
            Assert.Equal(1, one);
            Assert.Equal(2, two);
            Assert.Equal(3, three);
            Assert.Equal(4, four);
            Assert.Equal(5, five);
            Assert.Equal(6, six);
            Assert.Equal(7, seven);
            Assert.Equal(8, eight);
            Assert.Equal(9, nine);
            Assert.Equal(10, ten);

            var result2 = await _edgedb.QuerySingleAsync<(long one, long two)>("select (one := 1, two := 2)", token: _getToken());
            Assert.Equal(1, result2.one);
            Assert.Equal(2, result2.two);
        }

        [Fact]
        public async Task SetQueries()
        {
            var result = await _edgedb.QueryAsync<long>("select {1,2}", token: _getToken());
            Assert.Equal(1, result.First());
            Assert.Equal(2, result.Last());
        }

        [Fact]
        public async Task DisconnectAndReconnect()
        {
            // using raw client for this one,
            var client = await _edgedb.GetOrCreateClientAsync(token: _getToken());

            // disconnect should close the underlying connection, and remove alloc'd resources for said connection.
            await client.DisconnectAsync(token: _getToken());

            // should run just fine, restarting the underlying connection.
            var str = await client.QueryRequiredSingleAsync<string>("select \"Hello, EdgeDB.Net!\"", token: _getToken());

            Assert.Equal("Hello, EdgeDB.Net!", str);
        }

        [Fact]
        public void StateChange()
        {
            var client2 = _edgedb.WithConfig(x => x.DDLPolicy = DDLPolicy.AlwaysAllow);

            Assert.Null(_edgedb.Config.DDLPolicy);
            Assert.Equal(DDLPolicy.AlwaysAllow, client2.Config.DDLPolicy);

            var client3 = client2.WithModule("test_module");

            Assert.Null(_edgedb.Config.DDLPolicy);
            Assert.Equal(DDLPolicy.AlwaysAllow, client2.Config.DDLPolicy);
            Assert.Equal("test_module", client3.Module);
            Assert.NotEqual("test_module", client2.Module);
            Assert.NotEqual("test_module", _edgedb.Module);
        }

        private async Task TestScalarQuery<TResult>(string select, TResult expected)
        {
            var result = await _edgedb.QuerySingleAsync<TResult>($"select {select}", token: _getToken());
           
            switch(result)
            {
                case byte[] bt:
                    Assert.True(bt.SequenceEqual((expected as byte[])!));
                    break;

                default:
                    Assert.Equal(expected, result);
                    break;
            }
        }
    }
}
