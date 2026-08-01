using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TCJ.Empty.Products;

public sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");

        builder.HasKey(product => product.Id);

        builder.Property(product => product.Name)
               .HasMaxLength(200)
               .IsRequired();

        builder.Property(product => product.Price)
               .HasPrecision(18, 2);

        builder.HasIndex(product => product.Name);
    }
}
