namespace TCJ.AspNetCore.IntegrationTests.Fixtures;

[CollectionDefinition("ASP.NET Core integration")]
public sealed class AspNetCoreIntegrationCollection : ICollectionFixture<TcjWebApplicationFactory>
{
    public const string Name = "ASP.NET Core integration";
}
