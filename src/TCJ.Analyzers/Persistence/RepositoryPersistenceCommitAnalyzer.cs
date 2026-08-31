using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace TCJ.Analyzers.Persistence;

/// <summary>
/// Reports persistence commits performed from concrete TCJ repository implementations.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepositoryPersistenceCommitAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ1000";

    private const string Category = "TCJ.Persistence";
    private const string RepositoryMetadataName = "TCJ.EntityFrameworkCore.Repositories.IRepository";
    private const string UnitOfWorkMetadataName = "TCJ.EntityFrameworkCore.UnitOfWork.IUnitOfWork";
    private const string DbContextMetadataName = "Microsoft.EntityFrameworkCore.DbContext";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Repository implementation owns the persistence commit boundary",
        "Repository implementation '{0}' calls '{1}'. Repositories must stage changes and leave persistence commits to the application/use-case boundary",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "TCJ repositories stage persistence operations but do not own the commit boundary. Concrete repository implementations must not call Entity Framework Core DbContext.SaveChanges/SaveChangesAsync or TCJ IUnitOfWork.SaveChangesAsync.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(Rule);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(RegisterCompilationActions);
    }

    private static void RegisterCompilationActions(CompilationStartAnalysisContext context)
    {
        INamedTypeSymbol? repository = context.Compilation.GetTypeByMetadataName(RepositoryMetadataName);
        if (repository is null)
        {
            return;
        }

        INamedTypeSymbol? dbContext = context.Compilation.GetTypeByMetadataName(DbContextMetadataName);
        ImmutableArray<IMethodSymbol> dbContextCommitMethods = GetDbContextCommitMethods(dbContext);

        INamedTypeSymbol? unitOfWork = context.Compilation.GetTypeByMetadataName(UnitOfWorkMetadataName);
        ImmutableArray<IMethodSymbol> unitOfWorkCommitMethods = GetUnitOfWorkCommitMethods(unitOfWork);

        if (dbContextCommitMethods.IsDefaultOrEmpty && unitOfWorkCommitMethods.IsDefaultOrEmpty)
        {
            return;
        }

        context.RegisterOperationAction(
            operationContext => AnalyzeInvocation(
                operationContext,
                repository,
                dbContextCommitMethods,
                unitOfWork,
                unitOfWorkCommitMethods),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol repository,
        ImmutableArray<IMethodSymbol> dbContextCommitMethods,
        INamedTypeSymbol? unitOfWork,
        ImmutableArray<IMethodSymbol> unitOfWorkCommitMethods)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        INamedTypeSymbol? containingType = context.ContainingSymbol.ContainingType;

        if (!IsConcreteRepositoryImplementation(containingType, repository))
        {
            return;
        }

        string? commitMethod = null;

        if (IsDbContextCommit(invocation.TargetMethod, dbContextCommitMethods))
        {
            commitMethod = $"DbContext.{GetRootMethodName(invocation.TargetMethod, dbContextCommitMethods)}";
        }
        else if (unitOfWork is not null
            && IsUnitOfWorkCommit(invocation, unitOfWork, unitOfWorkCommitMethods))
        {
            commitMethod = "IUnitOfWork.SaveChangesAsync";
        }

        if (commitMethod is null)
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetInvocationNameLocation(invocation),
                containingType!.Name,
                commitMethod));
    }

    private static ImmutableArray<IMethodSymbol> GetDbContextCommitMethods(INamedTypeSymbol? dbContext)
    {
        if (dbContext is null)
        {
            return ImmutableArray<IMethodSymbol>.Empty;
        }

        return dbContext.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => method.Name is "SaveChanges" or "SaveChangesAsync")
            .ToImmutableArray();
    }

    private static ImmutableArray<IMethodSymbol> GetUnitOfWorkCommitMethods(INamedTypeSymbol? unitOfWork)
    {
        if (unitOfWork is null)
        {
            return ImmutableArray<IMethodSymbol>.Empty;
        }

        return unitOfWork.GetMembers("SaveChangesAsync")
            .OfType<IMethodSymbol>()
            .ToImmutableArray();
    }

    private static bool IsConcreteRepositoryImplementation(
        INamedTypeSymbol? type,
        INamedTypeSymbol repository)
        => type is { TypeKind: TypeKind.Class, IsAbstract: false }
            && type.AllInterfaces.Any(
                implemented => SymbolEqualityComparer.Default.Equals(implemented, repository));

    private static bool IsDbContextCommit(
        IMethodSymbol targetMethod,
        ImmutableArray<IMethodSymbol> dbContextCommitMethods)
    {
        if (targetMethod.ReducedFrom is not null)
        {
            return false;
        }

        for (IMethodSymbol? current = targetMethod; current is not null; current = current.OverriddenMethod)
        {
            if (dbContextCommitMethods.Any(
                candidate => SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition,
                    candidate.OriginalDefinition)))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetRootMethodName(
        IMethodSymbol targetMethod,
        ImmutableArray<IMethodSymbol> dbContextCommitMethods)
    {
        for (IMethodSymbol? current = targetMethod; current is not null; current = current.OverriddenMethod)
        {
            IMethodSymbol? match = dbContextCommitMethods.FirstOrDefault(
                candidate => SymbolEqualityComparer.Default.Equals(
                    current.OriginalDefinition,
                    candidate.OriginalDefinition));

            if (match is not null)
            {
                return match.Name;
            }
        }

        return targetMethod.Name;
    }

    private static bool IsUnitOfWorkCommit(
        IInvocationOperation invocation,
        INamedTypeSymbol unitOfWork,
        ImmutableArray<IMethodSymbol> unitOfWorkCommitMethods)
    {
        IMethodSymbol targetMethod = invocation.TargetMethod;

        if (targetMethod.ReducedFrom is not null || unitOfWorkCommitMethods.IsDefaultOrEmpty)
        {
            return false;
        }

        foreach (IMethodSymbol unitOfWorkCommitMethod in unitOfWorkCommitMethods)
        {
            if (SymbolEqualityComparer.Default.Equals(
                targetMethod.OriginalDefinition,
                unitOfWorkCommitMethod.OriginalDefinition))
            {
                return true;
            }

            if (targetMethod.ExplicitInterfaceImplementations.Any(
                implemented => SymbolEqualityComparer.Default.Equals(
                    implemented.OriginalDefinition,
                    unitOfWorkCommitMethod.OriginalDefinition)))
            {
                return true;
            }
        }

        if (invocation.Instance?.Type is not INamedTypeSymbol receiverType
            || !ImplementsInterface(receiverType, unitOfWork))
        {
            return false;
        }

        foreach (IMethodSymbol unitOfWorkCommitMethod in unitOfWorkCommitMethods)
        {
            ISymbol? implementation = receiverType.FindImplementationForInterfaceMember(unitOfWorkCommitMethod);
            if (implementation is IMethodSymbol implementationMethod
                && AreSameMethodOrOverride(targetMethod, implementationMethod))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, INamedTypeSymbol interfaceType)
        => SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, interfaceType.OriginalDefinition)
            || type.AllInterfaces.Any(
                implemented => SymbolEqualityComparer.Default.Equals(
                    implemented.OriginalDefinition,
                    interfaceType.OriginalDefinition));

    private static bool AreSameMethodOrOverride(IMethodSymbol left, IMethodSymbol right)
    {
        for (IMethodSymbol? current = left; current is not null; current = current.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(
                current.OriginalDefinition,
                right.OriginalDefinition))
            {
                return true;
            }
        }

        for (IMethodSymbol? current = right; current is not null; current = current.OverriddenMethod)
        {
            if (SymbolEqualityComparer.Default.Equals(
                current.OriginalDefinition,
                left.OriginalDefinition))
            {
                return true;
            }
        }

        return false;
    }

    private static Location GetInvocationNameLocation(IInvocationOperation invocation)
    {
        if (invocation.Syntax is not InvocationExpressionSyntax invocationSyntax)
        {
            return invocation.Syntax.GetLocation();
        }

        return invocationSyntax.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.GetLocation(),
            MemberBindingExpressionSyntax memberBinding => memberBinding.Name.GetLocation(),
            _ => invocationSyntax.Expression.GetLocation(),
        };
    }
}
