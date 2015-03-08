using System.Net;
using System.Net.Http;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Nowin;
using NUnit.Framework;

namespace NowinTests.ExecutionContext_Tests
{
    public class ExecutionContextFlowing
    {
        private void ResetExecutionContextFlowForTestThread()
        {
            if (ExecutionContext.IsFlowSuppressed())
                ExecutionContext.RestoreFlow();
        }

        [TestCase(ExecutionContextFlow.SuppressAlways)]
        [TestCase(ExecutionContextFlow.Flow)]
        [TestCase(ExecutionContextFlow.SuppressOnAsync)]
        public void ExecutionContextOnSetupThread(ExecutionContextFlow flow)
        {
            ResetExecutionContextFlowForTestThread();

            CallContext.LogicalSetData("test.data", "test.value");
            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .Build();
            server.Start();
            server.Dispose();
            Assert.AreEqual(
                "test.value",
                CallContext.LogicalGetData("test.data") as string);
        }

        [TestCase(ExecutionContextFlow.SuppressAlways, null)]
        [TestCase(ExecutionContextFlow.Flow, "test.value")]
        [TestCase(ExecutionContextFlow.SuppressOnAsync, "test.value")]
        public void ExecutionContextFlowFromSetupThread(ExecutionContextFlow flow, string expectedLogicalContextValue)
        {
            ResetExecutionContextFlowForTestThread();

            CallContext.LogicalSetData("test.data", "test.value");

            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .Build();
            server.Start();
            server.Dispose();
            

            var separateThreadValue = Task.Factory.StartNew(() => CallContext.LogicalGetData("test.data") as string);
            //Assert.True(ExecutionContext.IsFlowSuppressed());
            Assert.AreEqual(expectedLogicalContextValue, separateThreadValue.Result);
        }

        [TestCase(ExecutionContextFlow.Flow, "test.value")]
        [TestCase(ExecutionContextFlow.SuppressAlways, null)]
        [TestCase(ExecutionContextFlow.SuppressOnAsync, null)]
        public void ExecutionContextFlowToOwinApp(ExecutionContextFlow flow, string expectedValue)
        {
            ResetExecutionContextFlowForTestThread();

            string applicationValue = null;
            CallContext.LogicalSetData("test.data", "test.value");

            var server = new ServerBuilder()
                .SetEndPoint(new IPEndPoint(IPAddress.Loopback, 8082))
                .SetExecutionContextFlow(flow)
                .SetOwinApp(env=> AppReadingLogicalCallContext(out applicationValue))
                .Build();
            server.Start();
            var response = new HttpClient().GetAsync("http://localhost:8082").Result;
            server.Dispose();

            Assert.AreEqual(expectedValue, applicationValue);
        }

        private static Task AppReadingLogicalCallContext(out string applicationValue)
        {
            applicationValue = CallContext.LogicalGetData("test.data") as string;
            return Task.FromResult(0);
        }
    }
}