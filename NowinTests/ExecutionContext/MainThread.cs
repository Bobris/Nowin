using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Nowin;
using Xunit;

namespace NowinTests.ExecutionContext_Tests
{
    public class ExecutionContextFlowing
    {
        private void ResetExecutionContextFlowForTestThread()
        {
            if (ExecutionContext.IsFlowSuppressed())
                ExecutionContext.RestoreFlow();
        }
        [Theory]
        [InlineData(ExecutionContextFlow.SuppressAlways)]
        [InlineData(ExecutionContextFlow.Flow)]
        [InlineData(ExecutionContextFlow.SuppressOnAsync)]
        public void ExecutionContextOnSetupThread(ExecutionContextFlow flow)
        {
            ResetExecutionContextFlowForTestThread();

            CallContext.LogicalSetData("test.data", "test.value");
            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .SetRetrySocketBindingTime(System.TimeSpan.FromSeconds(4))
                .Build();
            server.Start();
            server.Dispose();
            Assert.Equal(
                "test.value",
                CallContext.LogicalGetData("test.data") as string);
        }

        [Theory]
        [InlineData(ExecutionContextFlow.SuppressAlways, null)]
        [InlineData(ExecutionContextFlow.Flow, "test.value")]
        [InlineData(ExecutionContextFlow.SuppressOnAsync, "test.value")]
        public void ExecutionContextFlowFromSetupThread(ExecutionContextFlow flow, string expectedLogicalContextValue)
        {
            ResetExecutionContextFlowForTestThread();

            CallContext.LogicalSetData("test.data", "test.value");

            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .SetRetrySocketBindingTime(System.TimeSpan.FromSeconds(4))
                .Build();
            server.Start();
            server.Dispose();
            

            var separateThreadValue = Task.Factory.StartNew(() => CallContext.LogicalGetData("test.data") as string);
            //Assert.True(ExecutionContext.IsFlowSuppressed());
            Assert.Equal(expectedLogicalContextValue, separateThreadValue.Result);
        }

        [Theory]
        [InlineData(ExecutionContextFlow.Flow, "test.value")]
        [InlineData(ExecutionContextFlow.SuppressAlways, null)]
        [InlineData(ExecutionContextFlow.SuppressOnAsync, null)]
        public void ExecutionContextFlowToOwinApp(ExecutionContextFlow flow, string expectedValue)
        {
            ResetExecutionContextFlowForTestThread();

            string applicationValue = null;
            CallContext.LogicalSetData("test.data", "test.value");

            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .SetOwinApp(env=> AppReadingLogicalCallContext(out applicationValue))
                .SetRetrySocketBindingTime(System.TimeSpan.FromSeconds(4))
                .Build();
            server.Start();
            using (var httpClient = new HttpClient())
            {
                var response = httpClient.GetAsync("http://localhost:8082").Result;
            }
            server.Dispose();

            Assert.Equal(expectedValue, applicationValue);
        }

        private static Task AppReadingLogicalCallContext(out string applicationValue)
        {
            applicationValue = CallContext.LogicalGetData("test.data") as string;
            return Task.FromResult(0);
        }
    }
}