using System;
using System.Diagnostics;

namespace Nowin
{
    public static class Log
    {
        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            LogInternal(message);
        }

        [Conditional("DEBUG")]
        public static void Write(string message, params object[] param)
        {
            LogInternal(string.Format(message, param));
        }

        static void LogInternal(string message)
        {
            Console.WriteLine("{0:O} {1}", DateTime.UtcNow, message);
        }
    }
}