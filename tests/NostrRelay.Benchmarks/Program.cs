using System.Reflection;
using BenchmarkDotNet.Running;

// BenchmarkSwitcher rather than three separate BenchmarkRunner.Run<T>() calls: lets you
// pick which benchmark class to run interactively, or pass a filter on the command line
// (e.g. `dotnet run -c Release -- --filter *Signature*`), without editing this file.
//
// Must be run in Release configuration; BenchmarkDotNet refuses to produce trustworthy
// numbers from a Debug build and will warn loudly if you try:
//   dotnet run -c Release --project tests/NostrRelay.Benchmarks
BenchmarkSwitcher.FromAssembly(Assembly.GetExecutingAssembly()).Run(args);
