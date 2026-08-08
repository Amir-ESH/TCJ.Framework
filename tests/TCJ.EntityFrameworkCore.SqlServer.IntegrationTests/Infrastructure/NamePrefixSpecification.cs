using TCJ.EntityFrameworkCore.Specifications;

namespace TCJ.EntityFrameworkCore.SqlServer.IntegrationTests.Infrastructure;

internal sealed class NamePrefixSpecification : Specification<SqlServerTestEntity>
{
    public NamePrefixSpecification(string prefix)
        : base(entity => entity.Name.StartsWith(prefix))
    {
        ApplyOrderBy(entity => entity.Name);
    }
}
