namespace Flux
{
    using System.Collections.Generic;

    internal static class DictionaryExtensions
    {
        public static T GetValueOrDefault<T>(this IDictionary<string, object> dictionary, string key)
        {
            return GetValueOrDefault(dictionary, key, default(T));
        }

        public static T GetValueOrDefault<T>(this IDictionary<string, object> dictionary, string key, T defaultValue)
        {
            object value;
            if (dictionary.TryGetValue(key, out value))
            {
                return (T) value;
            }
            return defaultValue;
        }
    }
}