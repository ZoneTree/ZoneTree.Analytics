namespace ZoneTree.Analytics;

// The HTTP integration must distinguish a policy rejection from temporary unavailability.
internal enum IngestionResult
{
  Accepted,
  Filtered,
  Unavailable
}
