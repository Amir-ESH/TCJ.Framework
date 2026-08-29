using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class ValueObjectConversionTests
{
    [Fact]
    public void Generated_registration_is_idempotent_and_conflicting_registrations_are_rejected()
    {
        var registry = new ValueObjectConversionRegistry();

        ValueObjectConversionRegistry first = registry.Register<ModelCode, string>(
            ModelCode.ValueObjectConversion.ToBackingValue,
            ModelCode.ValueObjectConversion.FromBackingValue);
        ValueObjectConversionRegistry duplicate = registry.Register<ModelCode, string>(
            ModelCode.ValueObjectConversion.ToBackingValue,
            ModelCode.ValueObjectConversion.FromBackingValue);

        Assert.Same(registry, first);
        Assert.Same(registry, duplicate);

        InvalidOperationException expressionsConflict = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<ModelCode, string>(
                static value => value.Value,
                static value => ModelCode.Create(value).Value));
        Assert.Contains("different conversion expressions", expressionsConflict.Message, StringComparison.Ordinal);

        InvalidOperationException backingConflict = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<ModelCode, int>(
                static value => value.Value.Length,
                static _ => default));
        Assert.Contains("already registered with backing type", backingConflict.Message, StringComparison.Ordinal);

        NotSupportedException unsupported = Assert.Throws<NotSupportedException>(() =>
            new ValueObjectConversionRegistry().Register<ModelCode, DateTime>(
                static _ => default,
                static _ => default));
        Assert.Contains("Supported backing types are String, Guid, Int32, Int64, and Decimal", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyValueObjectConversions_configures_all_supported_backing_types_and_nullable_wrappers()
    {
        using var context = CreateModelContext();
        IEntityType entity = context.Model.FindEntityType(typeof(ValueObjectRecord))!;

        IProperty codeProperty = entity.FindProperty(nameof(ValueObjectRecord.Code))!;
        IProperty guidProperty = entity.FindProperty(nameof(ValueObjectRecord.GuidValue))!;
        IProperty intProperty = entity.FindProperty(nameof(ValueObjectRecord.IntValue))!;
        IProperty longProperty = entity.FindProperty(nameof(ValueObjectRecord.LongValue))!;
        IProperty decimalProperty = entity.FindProperty(nameof(ValueObjectRecord.DecimalValue))!;
        IProperty optionalProperty = entity.FindProperty(nameof(ValueObjectRecord.OptionalCode))!;

        AssertConverter<ModelCode, string>(codeProperty);
        AssertConverter<ModelGuidValue, Guid>(guidProperty);
        AssertConverter<ModelIntValue, int>(intProperty);
        AssertConverter<ModelLongValue, long>(longProperty);
        AssertConverter<ModelDecimalValue, decimal>(decimalProperty);
        AssertConverter<ModelCode, string>(optionalProperty);
        Assert.Equal(typeof(ModelCode?), optionalProperty.ClrType);

        ValueComparer comparer = codeProperty.GetValueComparer();
        ModelCode first = ModelCode.Create("abc").Value;
        ModelCode equal = ModelCode.Create(" ABC ").Value;
        ModelCode different = ModelCode.Create("xyz").Value;

        Assert.True(comparer.Equals(first, equal));
        Assert.False(comparer.Equals(first, different));
        Assert.Equal(first, Assert.IsType<ModelCode>(comparer.Snapshot(first)));
    }

    [Fact]
    public void Provider_materialization_reuses_normalization_and_validation_without_leaking_rejected_data()
    {
        using var context = CreateModelContext();
        IProperty property = context.Model.FindEntityType(typeof(ValueObjectRecord))!
            .FindProperty(nameof(ValueObjectRecord.Code))!;
        ValueConverter converter = property.GetValueConverter()!;

        var normalized = Assert.IsType<ModelCode>(converter.ConvertFromProvider("  abc  "));
        Assert.Equal("ABC", normalized.Value);

        const string rejectedValue = "secret-invalid-value";
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            converter.ConvertFromProvider(rejectedValue));

        Assert.Contains(typeof(ModelCode).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Review or migrate the stored database value", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedValue, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Rejected sensitive value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Record_struct_equality_supports_ef_change_tracking_without_a_custom_comparer()
    {
        using var context = CreateModelContext();
        var entity = new ValueObjectRecord
        {
            Id = 1,
            Code = ModelCode.Create("abc").Value,
            GuidValue = ModelGuidValue.Create(Guid.Parse("1e3a2c99-d60c-4d65-95e3-36f482f5eebd")).Value,
            IntValue = ModelIntValue.Create(42).Value,
            LongValue = ModelLongValue.Create(84).Value,
            DecimalValue = ModelDecimalValue.Create(12.50m).Value
        };

        context.Add(entity);
        context.SaveChanges();

        EntityEntry entry = context.Entry(entity);
        entity.Code = ModelCode.Create(" ABC ").Value;
        context.ChangeTracker.DetectChanges();
        Assert.False(entry.Property(nameof(ValueObjectRecord.Code)).IsModified);

        entity.Code = ModelCode.Create("xyz").Value;
        context.ChangeTracker.DetectChanges();
        Assert.True(entry.Property(nameof(ValueObjectRecord.Code)).IsModified);
    }

    [Fact]
    public void ApplyValueObjectConversions_is_idempotent_for_the_same_registry()
    {
        var options = new DbContextOptionsBuilder<IdempotentValueObjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new IdempotentValueObjectDbContext(options);
        IProperty property = context.Model.FindEntityType(typeof(ValueObjectRecord))!
            .FindProperty(nameof(ValueObjectRecord.Code))!;

        AssertConverter<ModelCode, string>(property);
    }

    [Fact]
    public void ApplyValueObjectConversions_rejects_a_preconfigured_conflicting_converter()
    {
        var options = new DbContextOptionsBuilder<ConflictingValueObjectDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new ConflictingValueObjectDbContext(options);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _ = context.Model);
        Assert.Contains(nameof(ValueObjectRecord.Code), exception.Message, StringComparison.Ordinal);
        Assert.Contains("conflicts with the registered Value Object conversion", exception.Message, StringComparison.Ordinal);
    }

    private static ValueObjectModelDbContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<ValueObjectModelDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new ValueObjectModelDbContext(options);
    }

    private static ValueObjectConversionRegistry CreateRegistry() =>
        new ValueObjectConversionRegistry()
            .Register<ModelCode, string>(
                ModelCode.ValueObjectConversion.ToBackingValue,
                ModelCode.ValueObjectConversion.FromBackingValue)
            .Register<ModelGuidValue, Guid>(
                ModelGuidValue.ValueObjectConversion.ToBackingValue,
                ModelGuidValue.ValueObjectConversion.FromBackingValue)
            .Register<ModelIntValue, int>(
                ModelIntValue.ValueObjectConversion.ToBackingValue,
                ModelIntValue.ValueObjectConversion.FromBackingValue)
            .Register<ModelLongValue, long>(
                ModelLongValue.ValueObjectConversion.ToBackingValue,
                ModelLongValue.ValueObjectConversion.FromBackingValue)
            .Register<ModelDecimalValue, decimal>(
                ModelDecimalValue.ValueObjectConversion.ToBackingValue,
                ModelDecimalValue.ValueObjectConversion.FromBackingValue);

    private static void ConfigureEntity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ValueObjectRecord>(builder =>
        {
            builder.HasKey(static value => value.Id);
            builder.Property(static value => value.Code);
            builder.Property(static value => value.GuidValue);
            builder.Property(static value => value.IntValue);
            builder.Property(static value => value.LongValue);
            builder.Property(static value => value.DecimalValue);
            builder.Property(static value => value.OptionalCode);
        });
    }

    private static void AssertConverter<TValueObject, TBacking>(IProperty property)
    {
        ValueConverter converter = property.GetValueConverter()!;
        Assert.NotNull(converter);
        Assert.Equal(typeof(TValueObject), converter.ModelClrType);
        Assert.Equal(typeof(TBacking), converter.ProviderClrType);
    }

    private sealed class ValueObjectModelDbContext(DbContextOptions<ValueObjectModelDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.ApplyValueObjectConversions(CreateRegistry());
        }
    }

    private sealed class IdempotentValueObjectDbContext(DbContextOptions<IdempotentValueObjectDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            ValueObjectConversionRegistry registry = CreateRegistry();
            modelBuilder.ApplyValueObjectConversions(registry);
            modelBuilder.ApplyValueObjectConversions(registry);
        }
    }

    private sealed class ConflictingValueObjectDbContext(DbContextOptions<ConflictingValueObjectDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntity(modelBuilder);
            modelBuilder.Entity<ValueObjectRecord>()
                .Property(static value => value.Code)
                .HasConversion(
                    static value => value.Value.Length,
                    static _ => default);
            modelBuilder.ApplyValueObjectConversions(CreateRegistry());
        }
    }
}

[ValueObject<string>]
internal readonly partial record struct ModelCode
{
    private static string Normalize(string value) => value.Trim().ToUpperInvariant();

    private static Result Validate(string value)
        => value.Length == 3
            ? Result.Success()
            : Result.Failure(new ResultError("code.invalid", $"Rejected sensitive value '{value}'."));
}

[ValueObject<Guid>]
internal readonly partial record struct ModelGuidValue
{
    private static Result Validate(Guid value) => value != Guid.Empty
        ? Result.Success()
        : Result.Failure(new ResultError("guid.empty", "GUID must not be empty."));
}

[ValueObject<int>]
internal readonly partial record struct ModelIntValue
{
    private static Result Validate(int value) => value >= 0
        ? Result.Success()
        : Result.Failure(new ResultError("int.negative", "Value must be non-negative."));
}

[ValueObject<long>]
internal readonly partial record struct ModelLongValue
{
    private static Result Validate(long value) => value >= 0
        ? Result.Success()
        : Result.Failure(new ResultError("long.negative", "Value must be non-negative."));
}

[ValueObject<decimal>]
internal readonly partial record struct ModelDecimalValue
{
    private static Result Validate(decimal value) => value >= 0m
        ? Result.Success()
        : Result.Failure(new ResultError("decimal.negative", "Value must be non-negative."));
}

internal sealed class ValueObjectRecord
{
    public int Id { get; set; }

    public ModelCode Code { get; set; }

    public ModelGuidValue GuidValue { get; set; }

    public ModelIntValue IntValue { get; set; }

    public ModelLongValue LongValue { get; set; }

    public ModelDecimalValue DecimalValue { get; set; }

    public ModelCode? OptionalCode { get; set; }
}
