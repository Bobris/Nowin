using System;

namespace Nowin
{
    public sealed class NullDisposable : IDisposable
    {
        private NullDisposable()
        {
        }

        public static readonly IDisposable Instance = new NullDisposable();
        public void Dispose()
        {
        }
    }
}