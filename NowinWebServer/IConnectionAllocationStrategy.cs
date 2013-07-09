namespace NowinWebServer
{
    public interface IConnectionAllocationStrategy
    {
        int CalculateNewConnectionCount(int currentCount, int connectedCount);
    }
}