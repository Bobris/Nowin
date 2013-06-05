namespace Flux
{
    using System;

    internal static class DisposableEx
    {
        public static bool TryDispose(this IDisposable disposable)
        {
            if (disposable == null) return false;

            try
            {
                disposable.Dispose();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}