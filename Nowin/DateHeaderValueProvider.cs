using System;
using System.Threading;

namespace Nowin
{
    public class DateHeaderValueProvider : IDateHeaderValueProvider, IDisposable
    {
        readonly Timer _timer;
        volatile string _value;

        public DateHeaderValueProvider()
        {
            Update(null);
            _timer = new Timer(Update, null, 1000, 1000);
        }

        void Update(object state)
        {
            _value = DateTime.UtcNow.ToString("r");
        }

        public string Value => _value;

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}