namespace ChestTracker;

internal class Program
{
    public static async Task Main(string[] args)
    {
        await new Extension().RunAsync(CancellationToken.None);
    }
}