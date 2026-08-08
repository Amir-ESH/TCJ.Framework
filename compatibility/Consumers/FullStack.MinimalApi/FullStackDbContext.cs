using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TcjCompatibility.FullStackConsumer;

public sealed class FullStackDbContext(DbContextOptions<FullStackDbContext> options)
    : DbContext(options), IReadDbContext, IWriteDbContext
{
}
