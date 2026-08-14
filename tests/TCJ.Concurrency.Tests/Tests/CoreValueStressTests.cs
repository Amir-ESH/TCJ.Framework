using System.Collections.Concurrent;
using TCJ.Concurrency.Tests.Infrastructure;
using TCJ.Core.Identifiers;
using TCJ.Core.Results;

namespace TCJ.Concurrency.Tests.Tests;

[Trait("Category", "Concurrency")]
[Trait("Category", "Stress")]
public sealed class CoreValueStressTests
{
    [Fact]
    public Task ConcurrentResultReadsRemainStable()
    {
        Result<int> success = Result.Success(42);
        Result failure = Result.Failure(CommonErrors.Conflict("expected"));
        return StressRunner.RunAsync(nameof(ConcurrentResultReadsRemainStable), "core", _ =>
        {
            Assert.True(success.IsSuccess);
            Assert.Equal(42, success.Value);
            Assert.Empty(success.Errors);
            Assert.True(failure.IsFailure);
            Assert.Single(failure.Errors);
            Assert.Equal("expected", failure.FirstError?.Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public Task ConcurrentGuidGenerationProducesUniqueVersionSevenValues()
    {
        var generator = new GuidGenerator();
        var seen = new ConcurrentDictionary<Guid, byte>();
        return StressRunner.RunAsync(nameof(ConcurrentGuidGenerationProducesUniqueVersionSevenValues), "core", _ =>
        {
            Guid value = generator.CreateVersion7();
            Assert.Equal('7', value.ToString("D")[14]);
            Assert.True(seen.TryAdd(value, 0), $"Duplicate GUID v7 generated: {value}");
            return Task.CompletedTask;
        });
    }
}
