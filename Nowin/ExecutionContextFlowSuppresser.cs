using System;
using System.Threading;

namespace Nowin
{
    public static class ExecutionContextFlowSuppresser
    {
        static Func<IDisposable> _flow = () => NullDisposable.Instance;
        static Func<IDisposable> _suppressOnAsync = SuppressOnAsync;
        static Func<IDisposable> _suppressAlways = SuppressAlways;

        public static Func<IDisposable> CreateContextSuppresser(ExecutionContextFlow contextFlow)
        {
            switch (contextFlow)
            {
                case ExecutionContextFlow.SuppressAlways:
                    return _suppressAlways;
                case ExecutionContextFlow.SuppressOnAsync:
                    return _suppressOnAsync;
                case ExecutionContextFlow.Flow:
                    return _flow;
                default:
                    throw new ArgumentOutOfRangeException("contextFlow");
            }
        }

        private static IDisposable SuppressAlways()
        {
            if (!ExecutionContext.IsFlowSuppressed())
                ExecutionContext.SuppressFlow();
            return NullDisposable.Instance;
        }

        private static IDisposable SuppressOnAsync()
        {
            return ExecutionContext.IsFlowSuppressed()
                ? NullDisposable.Instance
                : ExecutionContext.SuppressFlow();
        }
    }
}