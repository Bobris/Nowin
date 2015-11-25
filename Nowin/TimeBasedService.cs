using System;
using System.Threading;

namespace Nowin
{
    public class TimeBasedService : ITimeBasedService, IDisposable
    {
        readonly uint _timeOut;
        readonly Timer _timer;
        volatile string _dateHeaderValue;
        volatile uint _currentSecond;

        public TimeBasedService(uint timeOut)
        {
            _timeOut = timeOut;
            _currentSecond = 0;
            Update(null);
            _timer = new Timer(Update, null, 1000, 1000);
        }

        void Update(object state)
        {
            var now = DateTime.UtcNow;
            _currentSecond++;
            _dateHeaderValue = now.ToString("r");
        }

        public string DateHeaderValue => _dateHeaderValue;

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}