using Microsoft.EntityFrameworkCore;
using TCJ.EntityFrameworkCore.Abstractions;

namespace TcjUpgrade.FullStackConsumer;

public sealed class FullStackDbContext(DbContextOptions<FullStackDbContext> options) : DbContext(options), IReadDbContext, IWriteDbContext { }
