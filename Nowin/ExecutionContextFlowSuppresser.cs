using System;
using System.Threading;

namespace Nowin
{
    public static class ExecutionContextFlowSuppresser
    {
        static readonly Func<IDisposable> _flow = () => NullDisposable.Instance;
        static readonly Func<IDisposable> _suppressOnAsync = SuppressOnAsync;
        static readonly Func<IDisposable> _suppressAlways = SuppressAlways;

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
                    throw new ArgumentOutOfRangeException(nameof(contextFlow));
            }
        }

        static IDisposable SuppressAlways()
        {
            if (!ExecutionContext.IsFlowSuppressed())
                ExecutionContext.SuppressFlow();
            return NullDisposable.Instance;
        }

        static IDisposable SuppressOnAsync()
        {
            return ExecutionContext.IsFlowSuppressed()
                ? NullDisposable.Instance
                : ExecutionContext.SuppressFlow();
        }
    }
}