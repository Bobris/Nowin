using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nowin
{
    internal sealed partial class OwinEnvironment : IDictionary<string, object>
    {
        readonly OwinHandler _handler;
        IHttpLayerCallback _callback;

        static readonly IDictionary<string, object> WeakNilEnvironment = new NilDictionary();
        IDictionary<string, object> _extra;

        public OwinEnvironment(OwinHandler handler)
        {
            _handler = handler;
        }

        public void Reset()
        {
            _callback = _handler.Callback;
            _extra = WeakNilEnvironment;
            PropertiesReset();
        }

        internal IDictionary<string, object> Extra
        {
            get { return _extra; }
        }

        IDictionary<string, object> StrongExtra
        {
            get
            {
                if (_extra == WeakNilEnvironment)
                {
                    Interlocked.CompareExchange(ref _extra, new Dictionary<string, object>(), WeakNilEnvironment);
                }
                return _extra;
            }
        }

        internal bool IsExtraDictionaryCreated
        {
            get { return _extra != WeakNilEnvironment; }
        }

        class NilDictionary : IDictionary<string, object>
        {
            private static readonly string[] EmptyKeys = new string[0];
            private static readonly object[] EmptyValues = new object[0];
            private static readonly IEnumerable<KeyValuePair<string, object>> EmptyKeyValuePairs = Enumerable.Empty<KeyValuePair<string, object>>();

            public int Count
            {
                get { return 0; }
            }

            public bool IsReadOnly
            {
                get { return false; }
            }

            public ICollection<string> Keys
            {
                get { return EmptyKeys; }
            }

            public ICollection<object> Values
            {
                get { return EmptyValues; }
            }

            public object this[string key]
            {
                get { throw new KeyNotFoundException(key); }
                set { throw new InvalidOperationException(); }
            }

            public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
            {
                return EmptyKeyValuePairs.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return EmptyKeyValuePairs.GetEnumerator();
            }

            public void Add(KeyValuePair<string, object> item)
            {
                throw new InvalidOperationException();
            }

            public void Clear()
            {
            }

            public bool Contains(KeyValuePair<string, object> item)
            {
                return false;
            }

            public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
            {
            }

            public bool Remove(KeyValuePair<string, object> item)
            {
                return false;
            }

            public bool ContainsKey(string key)
            {
                return false;
            }

            public void Add(string key, object value)
            {
                throw new InvalidOperationException();
            }

            public bool Remove(string key)
            {
                return false;
            }

            public bool TryGetValue(string key, out object value)
            {
                value = null;
                return false;
            }
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            return PropertiesEnumerable().Concat(Extra).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(KeyValuePair<string, object> item)
        {
            Add(item.Key, item.Value);
        }

        public void Clear()
        {
            foreach (var key in PropertiesKeys())
            {
                PropertiesTryRemove(key);
            }
            Extra.Clear();
        }

        public bool Contains(KeyValuePair<string, object> item)
        {
            object value;
            return TryGetValue(item.Key, out value) && Equals(value, item.Value);
        }

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            PropertiesEnumerable().Concat(Extra).ToArray().CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<string, object> item)
        {
            return Contains(item) && Remove(item.Key);
        }

        public int Count
        {
            get { return PropertiesKeys().Count() + Extra.Count; }
        }

        public bool IsReadOnly
        {
            get { return false; }
        }

        public bool ContainsKey(string key)
        {
            return PropertiesContainsKey(key) || Extra.ContainsKey(key);
        }

        public void Add(string key, object value)
        {
            if (!PropertiesTrySetValue(key, value))
            {
                StrongExtra.Add(key, value);
            }
        }

        public bool Remove(string key)
        {
            return PropertiesTryRemove(key) || Extra.Remove(key);
        }

        public bool TryGetValue(string key, out object value)
        {
            return PropertiesTryGetValue(key, out value) || Extra.TryGetValue(key, out value);
        }

        public object this[string key]
        {
            get
            {
                object value;
                return PropertiesTryGetValue(key, out value) ? value : Extra[key];
            }
            set
            {
                if (!PropertiesTrySetValue(key, value))
                {
                    StrongExtra[key] = value;
                }
            }
        }

        public ICollection<string> Keys
        {
            get { return PropertiesKeys().Concat(Extra.Keys).ToArray(); }
        }

        public ICollection<object> Values
        {
            get { return PropertiesValues().Concat(Extra.Values).ToArray(); }
        }
    }
}