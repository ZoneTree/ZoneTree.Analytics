var stats = analytics.GetStatistics(includePartitions: false);
Console.WriteLine($"Accepted: {stats.EventsAccepted}");
Console.WriteLine($"Processed: {stats.EventsProcessed}");
Console.WriteLine($"Queued: {stats.QueueLength}");
Console.WriteLine($"Dropped: {stats.EventsDropped}; Errors: {stats.Errors}");

