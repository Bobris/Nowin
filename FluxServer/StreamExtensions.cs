namespace Flux
{
    using System.IO;
    using System.Threading.Tasks;

    internal static class StreamExtensions
    {
        public static Task WriteAsync(this Stream stream, byte[] bytes, int offset, int count)
        {
            return Task.Factory.FromAsync(stream.BeginWrite, stream.EndWrite, bytes, offset, count, null, TaskCreationOptions.None);
        }
    }
}