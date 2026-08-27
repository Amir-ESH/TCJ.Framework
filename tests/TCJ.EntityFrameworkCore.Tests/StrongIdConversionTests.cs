using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TCJ.Core.StrongTypes;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;

namespace TCJ.EntityFrameworkCore.Tests;

public sealed class StrongIdConversionTests
{
    [Fact]
    public void Generated_registration_is_idempotent_and_conflicting_registrations_are_rejected()
    {
        var registry = new StrongIdConversionRegistry();

        StrongIdConversionRegistry first = registry.Register<ModelGuidId, Guid>(
            ModelGuidId.StrongIdConversion.ToBackingValue,
            ModelGuidId.StrongIdConversion.FromBackingValue);
        StrongIdConversionRegistry duplicate = registry.Register<ModelGuidId, Guid>(
            ModelGuidId.StrongIdConversion.ToBackingValue,
            ModelGuidId.StrongIdConversion.FromBackingValue);

        Assert.Same(registry, first);
        Assert.Same(registry, duplicate);

        InvalidOperationException expressionsConflict = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<ModelGuidId, Guid>(
                static value => value.Value,
                static value => new ModelGuidId(value)));
        Assert.Contains("different conversion expressions", expressionsConflict.Message, StringComparison.Ordinal);

        InvalidOperationException backingConflict = Assert.Throws<InvalidOperationException>(() =>
            registry.Register<ModelGuidId, int>(
                static value => value.Value.GetHashCode(),
                static _ => default));
        Assert.Contains("already registered with backing type", backingConflict.Message, StringComparison.Ordinal);

        NotSupportedException unsupported = Assert.Throws<NotSupportedException>(() =>
            new StrongIdConversionRegistry().Register<ModelGuidId, decimal>(
                static value => value.Value.GetHashCode(),
                static _ => default));
        Assert.Contains("Supported backing types are Guid, Int32, and Int64", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyStrongIdConversions_configures_keys_foreign_keys_nullable_wrappers_and_ordinary_properties()
    {
        using var context = CreateModelContext();
        IModel model = context.Model;

        IEntityType principal = model.FindEntityType(typeof(StrongIdPrincipal))!;
        IEntityType dependent = model.FindEntityType(typeof(StrongIdDependent))!;

        IProperty principalKey = principal.FindProperty(nameof(StrongIdPrincipal.Id))!;
        IProperty foreignKey = dependent.FindProperty(nameof(StrongIdDependent.PrincipalId))!;
        IProperty intProperty = principal.FindProperty(nameof(StrongIdPrincipal.Sequence))!;
        IProperty longProperty = principal.FindProperty(nameof(StrongIdPrincipal.Version))!;
        IProperty nullableProperty = principal.FindProperty(nameof(StrongIdPrincipal.OptionalId))!;

        AssertConverter<ModelGuidId, Guid>(principalKey);
        AssertConverter<ModelGuidId, Guid>(foreignKey);
        AssertConverter<ModelIntId, int>(intProperty);
        AssertConverter<ModelLongId, long>(longProperty);
        AssertConverter<ModelGuidId, Guid>(nullableProperty);

        Assert.Equal(typeof(ModelGuidId), principal.FindPrimaryKey()!.Properties.Single().ClrType);
        Assert.Equal(typeof(ModelGuidId), dependent.GetForeignKeys().Single().Properties.Single().ClrType);
        Assert.Equal(typeof(ModelGuidId?), nullableProperty.ClrType);

        ValueComparer comparer = principalKey.GetValueComparer();
        var first = new ModelGuidId(Guid.Parse("17b61456-0f05-4c49-b070-e8d53167c6aa"));
        var equal = new ModelGuidId(first.Value);
        var different = new ModelGuidId(Guid.Parse("56ccbbce-876e-4ad5-b1ee-ea552fb6b418"));

        Assert.True(comparer.Equals(first, equal));
        Assert.False(comparer.Equals(first, different));
        Assert.Equal(first, Assert.IsType<ModelGuidId>(comparer.Snapshot(first)));
    }

    [Fact]
    public void ApplyStrongIdConversions_is_idempotent_for_the_same_registry()
    {
        var options = new DbContextOptionsBuilder<IdempotentStrongIdDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new IdempotentStrongIdDbContext(options);

        IProperty property = context.Model.FindEntityType(typeof(StrongIdPrincipal))!
            .FindProperty(nameof(StrongIdPrincipal.Id))!;

        AssertConverter<ModelGuidId, Guid>(property);
    }

    [Fact]
    public void ApplyStrongIdConversions_rejects_a_preconfigured_conflicting_converter()
    {
        var options = new DbContextOptionsBuilder<ConflictingStrongIdDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        using var context = new ConflictingStrongIdDbContext(options);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _ = context.Model);
        Assert.Contains(nameof(StrongIdPrincipal.Id), exception.Message, StringComparison.Ordinal);
        Assert.Contains("conflicts with the registered Strong ID conversion", exception.Message, StringComparison.Ordinal);
    }

    private static StrongIdModelDbContext CreateModelContext()
    {
        var options = new DbContextOptionsBuilder<StrongIdModelDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new StrongIdModelDbContext(options);
    }

    private static StrongIdConversionRegistry CreateRegistry() =>
        new StrongIdConversionRegistry()
            .Register<ModelGuidId, Guid>(
                ModelGuidId.StrongIdConversion.ToBackingValue,
                ModelGuidId.StrongIdConversion.FromBackingValue)
            .Register<ModelIntId, int>(
                ModelIntId.StrongIdConversion.ToBackingValue,
                ModelIntId.StrongIdConversion.FromBackingValue)
            .Register<ModelLongId, long>(
                ModelLongId.StrongIdConversion.ToBackingValue,
                ModelLongId.StrongIdConversion.FromBackingValue);

    private static void ConfigureEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StrongIdPrincipal>(builder =>
        {
            builder.HasKey(static value => value.Id);
            builder.Property(static value => value.Sequence);
            builder.Property(static value => value.Version);
            builder.Property(static value => value.OptionalId);
        });

        modelBuilder.Entity<StrongIdDependent>(builder =>
        {
            builder.HasKey(static value => value.Id);
            builder.HasOne(static value => value.Principal)
                .WithMany(static value => value.Dependents)
                .HasForeignKey(static value => value.PrincipalId);
        });
    }

    private static void AssertConverter<TStrongId, TBacking>(IProperty property)
    {
        ValueConverter converter = property.GetValueConverter()!;
        Assert.NotNull(converter);
        Assert.Equal(typeof(TStrongId), converter.ModelClrType);
        Assert.Equal(typeof(TBacking), converter.ProviderClrType);
    }

    private sealed class StrongIdModelDbContext(DbContextOptions<StrongIdModelDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntities(modelBuilder);
            modelBuilder.ApplyStrongIdConversions(CreateRegistry());
        }
    }

    private sealed class IdempotentStrongIdDbContext(DbContextOptions<IdempotentStrongIdDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntities(modelBuilder);
            StrongIdConversionRegistry registry = CreateRegistry();
            modelBuilder.ApplyStrongIdConversions(registry);
            modelBuilder.ApplyStrongIdConversions(registry);
        }
    }

    private sealed class ConflictingStrongIdDbContext(DbContextOptions<ConflictingStrongIdDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureEntities(modelBuilder);
            modelBuilder.Entity<StrongIdPrincipal>()
                .Property(static value => value.Id)
                .HasConversion(
                    static value => value.Value.ToString("D"),
                    static value => new ModelGuidId(Guid.Parse(value)));
            modelBuilder.ApplyStrongIdConversions(CreateRegistry());
        }
    }
}

[StronglyTypedId<Guid>]
internal readonly partial record struct ModelGuidId;

[StronglyTypedId<int>]
internal readonly partial record struct ModelIntId;

[StronglyTypedId<long>]
internal readonly partial record struct ModelLongId;

internal sealed class StrongIdPrincipal
{
    public ModelGuidId Id { get; set; }

    public ModelIntId Sequence { get; set; }

    public ModelLongId Version { get; set; }

    public ModelGuidId? OptionalId { get; set; }

    public ICollection<StrongIdDependent> Dependents { get; } = [];
}

internal sealed class StrongIdDependent
{
    public int Id { get; set; }

    public ModelGuidId PrincipalId { get; set; }

    public StrongIdPrincipal Principal { get; set; } = null!;
}
