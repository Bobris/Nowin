using System;
using System.Diagnostics;

namespace Nowin
{
    public static class Log
    {
        [Conditional("DEBUG")]
        public static void Write(int id, string message)
        {
            LogInternal(id, message);
        }

        [Conditional("DEBUG")]
        public static void Write(int id, string message, params object[] param)
        {
            LogInternal(id, string.Format(message, param));
        }

        static void LogInternal(int id, string message)
        {
            Console.WriteLine("{0:O} ID{1,-5} {2}", DateTime.UtcNow, id, message);
        }
    }
}