using Xunit;

// Workbook's constructor calls SdfManager.ResetTables(), which clears process-wide
// static state shared by every sheet-defined function. Tests must not run concurrently.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
