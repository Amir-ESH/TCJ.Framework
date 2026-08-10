using Microsoft.EntityFrameworkCore.Infrastructure;

namespace TCJ.EntityFrameworkCore.SqlServer.Options;

/// <summary>
/// Configures the SQL Server provider defaults used by TCJ.
/// </summary>
public sealed class TcjSqlServerOptions
{
    /// <summary>
    /// Gets or sets whether SQL Server connection resiliency is enabled.
    /// The default value is <see langword="true"/>.
    /// </summary>
    public bool EnableRetryOnFailure { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of retry attempts when connection resiliency is enabled.
    /// The default value is 6; enabled retry configuration is bounded to the range 1 through 10.
    /// </summary>
    public int MaxRetryCount { get; set; } = 6;

    /// <summary>
    /// Gets or sets the maximum delay between retry attempts.
    /// The default and maximum value is 30 seconds when retry-on-failure is enabled.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the relational command timeout, in seconds.
    /// A <see langword="null"/> value keeps the provider default.
    /// </summary>
    public int? CommandTimeout { get; set; }

    /// <summary>
    /// Gets or sets the assembly containing Entity Framework Core migrations.
    /// A <see langword="null"/> or empty value keeps the provider default.
    /// </summary>
    public string? MigrationsAssembly { get; set; }

    /// <summary>
    /// Gets additional SQL Server error numbers that should be treated as transient.
    /// </summary>
    public ISet<int> AdditionalTransientErrorNumbers { get; } = new HashSet<int>();

    internal void Apply(SqlServerDbContextOptionsBuilder sqlServerOptionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(sqlServerOptionsBuilder);

        Validate();

        if (!string.IsNullOrWhiteSpace(MigrationsAssembly))
        {
            sqlServerOptionsBuilder.MigrationsAssembly(MigrationsAssembly.Trim());
        }

        if (CommandTimeout is int commandTimeout)
        {
            sqlServerOptionsBuilder.CommandTimeout(commandTimeout);
        }

        if (EnableRetryOnFailure)
        {
            sqlServerOptionsBuilder.EnableRetryOnFailure(
                MaxRetryCount,
                MaxRetryDelay,
                AdditionalTransientErrorNumbers);
        }
    }

    private void Validate()
    {
        if (EnableRetryOnFailure && MaxRetryCount is < 1 or > 10)
        {
            throw new InvalidOperationException(
                $"{nameof(MaxRetryCount)} must be between 1 and 10 when retry-on-failure is enabled.");
        }

        if (EnableRetryOnFailure && (MaxRetryDelay <= TimeSpan.Zero || MaxRetryDelay > TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException(
                $"{nameof(MaxRetryDelay)} must be greater than zero and no more than 30 seconds when retry-on-failure is enabled.");
        }

        if (CommandTimeout is <= 0)
        {
            throw new InvalidOperationException(
                $"{nameof(CommandTimeout)} must be greater than zero when specified.");
        }
    }
}
