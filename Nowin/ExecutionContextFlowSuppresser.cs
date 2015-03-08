using System;
using System.Threading;

namespace Nowin
{
    public static class ExecutionContextFlowSuppresser
    {
        public static Func<IDisposable> CreateContextSuppresser(ExecutionContextFlow contextFlow)
        {
            if (contextFlow == ExecutionContextFlow.Flow)
                return () => NullDisposable.Instance;
            if (contextFlow == ExecutionContextFlow.SuppressOnAsync)
                return SuppressOnAsync;
            return SuppressAlways;
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