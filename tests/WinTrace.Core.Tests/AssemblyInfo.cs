// TraceSession sets the static TraceRecorder.Current (one traced test at a time, as documented), so tests that create
// sessions must not run in parallel.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
