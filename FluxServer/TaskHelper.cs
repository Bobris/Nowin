namespace Flux
{
    using System.Threading.Tasks;

    internal class TaskHelper
    {
        public static Task Completed()
        {
            var tcs = new TaskCompletionSource<int>();
            tcs.SetResult(0);
            return tcs.Task;
        }
    }
}