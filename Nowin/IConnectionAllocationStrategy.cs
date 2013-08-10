namespace Nowin
{
    public interface IConnectionAllocationStrategy
    {
        int CalculateNewConnectionCount(int currentCount, int connectedCount);
    }
}