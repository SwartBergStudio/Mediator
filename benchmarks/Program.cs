using BenchmarkDotNet.Running;
using Mediator.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(QuickBenchmarks).Assembly).Run(args);