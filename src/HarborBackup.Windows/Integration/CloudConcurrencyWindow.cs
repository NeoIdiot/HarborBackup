namespace HarborBackup.Windows.Integration;

internal sealed class CloudConcurrencyWindow(int maximum, long initialThrottles)
{
    private long previous = initialThrottles;
    private readonly int ceiling = maximum;
    private int successful;
    public int Limit { get; private set; } = maximum;
    public int Reductions { get; private set; }
    public void Observe(long throttles, int completed, bool success)
    {
        if (throttles > previous)
        {
            var reduced = Math.Max(1, Limit / 2);
            if (reduced < Limit) Reductions++;
            Limit = reduced; successful = 0;
        }
        else if (success)
        {
            successful += completed;
            if (successful >= 16) { Limit = Math.Min(ceiling, Limit + 1); successful = 0; }
        }
        else successful = 0;
        previous = throttles;
    }
}
