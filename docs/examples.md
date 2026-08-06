# Validated consumer examples

The fences marked `csharp validate` are extracted by `eng/verify-documentation.py` and compiled against project references for all five production packages. Pseudocode must use an ordinary `csharp` fence and must not be marked for validation.

## Result usage

```csharp validate id=result-usage
using TCJ.Core.Results;

namespace DocumentationExamples.ResultUsage;

public static class ResultExample
{
    public static Result<int> ParsePositive(string text)
    {
        if (!int.TryParse(text, out int value) || value <= 0)
        {
            return Result.Failure<int>(CommonErrors.Validation("A positive integer is required."));
        }

        return Result.Success(value);
    }
}
```

## Dependency registration

```csharp validate id=dependency-registration
using Microsoft.Extensions.DependencyInjection;
using TCJ.DependencyInjection.Extensions;
using TCJ.DependencyInjection.Lifetimes;

namespace DocumentationExamples.DependencyRegistration;

public sealed class RequestClock : IScopedDependency
{
}

public static class DependencyRegistrationExample
{
    public static IServiceCollection AddApplicationServices(IServiceCollection services) =>
        services.AddTcjDependencyInjection(typeof(DependencyRegistrationExample).Assembly);
}
```

## Repository and Unit of Work

```csharp validate id=repository-unit-of-work
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Repositories;
using TCJ.EntityFrameworkCore.UnitOfWork;

namespace DocumentationExamples.RepositoryUnitOfWork;

public sealed class Order(long id) : Entity<long>(id)
{
}

public static class OrderWriter
{
    public static async Task AddAsync(
        IRepository<Order> repository,
        IUnitOfWork unitOfWork,
        Order order,
        CancellationToken cancellationToken)
    {
        await repository.AddAsync(order, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
```

## Specification usage

```csharp validate id=specification-usage
using TCJ.Core.Entities;
using TCJ.EntityFrameworkCore.Specifications;

namespace DocumentationExamples.SpecificationUsage;

public sealed class Customer(long id, bool active) : Entity<long>(id)
{
    public bool IsActive { get; } = active;
}

public sealed class ActiveCustomers : Specification<Customer>
{
    public ActiveCustomers() : base(customer => customer.IsActive)
    {
        ApplyOrderBy(customer => customer.Id);
    }
}
```

## Entity Framework Core registration

```csharp validate id=efcore-registration
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.Extensions;

namespace DocumentationExamples.EntityFrameworkCoreRegistration;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
}

public static class PersistenceRegistration
{
    public static IServiceCollection AddPersistence(IServiceCollection services) =>
        services.AddTcjEntityFrameworkCore<AppDbContext>(options => { });
}
```

## SQL Server registration

```csharp validate id=sqlserver-registration
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TCJ.EntityFrameworkCore.Abstractions;
using TCJ.EntityFrameworkCore.SqlServer.Extensions;

namespace DocumentationExamples.SqlServerRegistration;

public sealed class SqlAppDbContext(DbContextOptions<SqlAppDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
}

public static class SqlPersistenceRegistration
{
    public static IServiceCollection AddSqlPersistence(
        IServiceCollection services,
        string connectionString) =>
        services.AddTcjSqlServer<SqlAppDbContext>(connectionString);
}
```

## ASP.NET Core registration and exception handling

```csharp validate id=aspnetcore-registration
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using TCJ.AspNetCore.Extensions;

namespace DocumentationExamples.AspNetCoreRegistration;

public static class WebRegistration
{
    public static void ConfigureServices(IServiceCollection services) =>
        services.AddTcjAspNetCore(options => options.IncludeExceptionDetails = false);

    public static IApplicationBuilder ConfigurePipeline(IApplicationBuilder app) =>
        app.UseTcjAspNetCore();
}
```

## Domain-event dispatching

```csharp validate id=domain-event-dispatch
using TCJ.Core.DomainEvents;

namespace DocumentationExamples.DomainEvents;

public sealed record OrderPlaced(DateTimeOffset OccurredOn) : IDomainEvent;

public sealed class OrderPlacedHandler : IDomainEventHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced domainEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public static class EventDispatchExample
{
    public static Task DispatchAsync(
        IDomainEventDispatcher dispatcher,
        IDomainEvent domainEvent,
        CancellationToken cancellationToken) =>
        dispatcher.DispatchAsync([domainEvent], cancellationToken);
}
```
