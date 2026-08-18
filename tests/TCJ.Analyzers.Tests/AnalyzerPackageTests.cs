using System.IO.Compression;
using System.Xml.Linq;
using TCJ.Analyzers.Tests.Infrastructure;

namespace TCJ.Analyzers.Tests;

public sealed class AnalyzerPackageTests
{
    private const string TestPackageVersion = "0.0.0-important9";

    [Fact]
    public async Task Package_installs_as_analyzer_assets_without_runtime_DLLs()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "TCJ.Analyzers.Tests",
            Guid.NewGuid().ToString("N"));
        string packageDirectory = Path.Combine(tempRoot, "packages");
        string consumerDirectory = Path.Combine(tempRoot, "consumer");

        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(consumerDirectory);

        try
        {
            await PackAnalyzerAsync(packageDirectory);

            string packagePath = Path.Combine(
                packageDirectory,
                $"TCJ.Analyzers.{TestPackageVersion}.nupkg");
            Assert.True(File.Exists(packagePath), $"Expected package '{packagePath}' was not produced.");

            AssertPackageLayout(packagePath);

            await WriteAndBuildConsumerAsync(consumerDirectory, packageDirectory);
            AssertRuntimeOutputIsClean(consumerDirectory);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static Task PackAnalyzerAsync(string packageDirectory)
        => ProcessRunner.RunAsync(
            "dotnet",
            [
                "pack",
                RepositoryLayout.Combine("eng/packaging/TCJ.Analyzers/TCJ.Analyzers.Package.csproj"),
                "--configuration",
                "Release",
                "--no-build",
                "--no-restore",
                "--output",
                packageDirectory,
                $"-p:Version={TestPackageVersion}",
                $"-p:PackageVersion={TestPackageVersion}",
            ],
            RepositoryLayout.Root.FullName);

    private static void AssertPackageLayout(string packagePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        string[] entries = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .ToArray();

        Assert.Contains("analyzers/dotnet/cs/TCJ.Analyzers.dll", entries);
        Assert.Contains("analyzers/dotnet/cs/TCJ.Analyzers.CodeFixes.dll", entries);
        Assert.Contains("README.md", entries);
        Assert.Contains("LICENSE.txt", entries);

        Assert.DoesNotContain(entries, entry => entry.StartsWith("lib/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, entry => entry.StartsWith("ref/", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            entries,
            entry => entry.StartsWith("analyzers/dotnet/cs/Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));

        ZipArchiveEntry nuspecEntry = archive.Entries.Single(entry =>
            entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));

        using Stream nuspecStream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(nuspecStream);
        Assert.Empty(nuspec.Descendants().Where(element => element.Name.LocalName == "dependency"));
    }

    private static async Task WriteAndBuildConsumerAsync(
        string consumerDirectory,
        string packageDirectory)
    {
        string projectPath = Path.Combine(consumerDirectory, "AnalyzerConsumer.csproj");
        string sourcePath = Path.Combine(consumerDirectory, "Program.cs");

        await File.WriteAllTextAsync(
            projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>

              <ItemGroup>
                <PackageReference Include="TCJ.Analyzers" Version="{{TestPackageVersion}}" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(sourcePath, "Console.WriteLine(\"Analyzer consumer\");\n");

        await ProcessRunner.RunAsync(
            "dotnet",
            [
                "restore",
                projectPath,
                "--source",
                packageDirectory,
                "--no-cache",
            ],
            consumerDirectory);

        await ProcessRunner.RunAsync(
            "dotnet",
            [
                "build",
                projectPath,
                "--configuration",
                "Release",
                "--no-restore",
            ],
            consumerDirectory);
    }

    private static void AssertRuntimeOutputIsClean(string consumerDirectory)
    {
        string outputDirectory = Path.Combine(consumerDirectory, "bin", "Release", "net10.0");
        Assert.True(Directory.Exists(outputDirectory));

        string[] runtimeDlls = Directory.GetFiles(outputDirectory, "*.dll")
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null)
            .Cast<string>()
            .ToArray();

        Assert.DoesNotContain(runtimeDlls, fileName =>
            fileName.StartsWith("TCJ.Analyzers", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(runtimeDlls, fileName =>
            fileName.StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase));
    }
}
