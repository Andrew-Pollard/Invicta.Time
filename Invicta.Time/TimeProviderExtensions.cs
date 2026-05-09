namespace Invicta.Time
{
    public static class TimeProviderExtensions
    {
        extension(TimeProvider provider)
        {
            public static TimeProvider HighResolution => HighResolutionTimeProvider.Instance;
        }
    }
}
