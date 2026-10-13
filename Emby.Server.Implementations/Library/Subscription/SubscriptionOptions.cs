using System;

public sealed class SubscriptionOptions
{
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromMinutes(5);

    public int PageSize { get; set; } = 2000;

    public int MaxConcurrency { get; set; } = 8;
}
