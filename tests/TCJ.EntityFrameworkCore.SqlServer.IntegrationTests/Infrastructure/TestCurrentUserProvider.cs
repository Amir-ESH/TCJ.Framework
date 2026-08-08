using TCJ.Core.Security;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal sealed class TestCurrentUserProvider(long? userId) : ICurrentUserProvider
{
    public long? UserId { get; } = userId;
}
