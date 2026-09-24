using Xunit;

// Some tests temporarily change the process-wide GameConfig (split force, map size...), and every
// GameWorld reads it - so test classes must not run concurrently. The whole suite takes ~1s.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
