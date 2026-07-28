using NUnit.Framework;

// The suite drives one shared app instance backed by a single SQL Server database, so keep
// the Playwright/NUnit runner single-threaded to avoid cross-test data races.
[assembly: LevelOfParallelism(1)]
