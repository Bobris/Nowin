namespace Nowin
{
    public class ConnectionAllocationStrategy : IConnectionAllocationStrategy
    {
        readonly int _startCount;
        readonly int _deltaCount;
        readonly int _maxCount;
        readonly int _keepFree;

        public ConnectionAllocationStrategy(int startCount, int deltaCount, int maxCount, int keepFree)
        {
            _startCount = startCount;
            _deltaCount = deltaCount;
            _maxCount = maxCount;
            _keepFree = keepFree;
        }

        public int CalculateNewConnectionCount(int currentCount, int connectedCount)
        {
            if (currentCount == 0) return _startCount;
            if (currentCount >= _maxCount) return 0;
            if (currentCount - connectedCount < _keepFree)
            {
                return _deltaCount;
            }
            return 0;
        }
    }
}