using System.Reflection;
using BenchmarkDotNet.Running;
using TCJ.Benchmarks.Configuration;

BenchmarkCatalog.WriteManifest();

BenchmarkSwitcher
    .FromAssembly(Assembly.GetExecutingAssembly())
    .Run(args, TcjBenchmarkConfig.Create());
