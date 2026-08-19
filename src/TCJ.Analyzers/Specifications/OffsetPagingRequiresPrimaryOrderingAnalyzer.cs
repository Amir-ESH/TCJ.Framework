using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TCJ.Analyzers.Specifications;

/// <summary>
/// Reports offset paging when a TCJ specification construction path has no provable primary ordering.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OffsetPagingRequiresPrimaryOrderingAnalyzer : DiagnosticAnalyzer
{
    internal const string DiagnosticId = "TCJ2000";

    private const string Category = "TCJ.Specifications";
    private const string SpecificationMetadataName =
        "TCJ.EntityFrameworkCore.Specifications.Specification`1";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Offset paging requires deterministic primary ordering",
        "Specification '{0}' applies offset paging without a provable primary ApplyOrderBy/ApplyOrderByDescending configuration. ApplyThenBy/ApplyThenByDescending alone do not establish primary ordering",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "TCJ offset paging should be paired with a deterministic primary ApplyOrderBy or ApplyOrderByDescending configuration in the same specification construction path. Secondary ApplyThenBy ordering cannot establish the primary order by itself.");

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
        INamedTypeSymbol? specification =
            context.Compilation.GetTypeByMetadataName(SpecificationMetadataName);
        if (specification is null)
        {
            return;
        }

        IMethodSymbol? applyPaging = GetMethod(specification, "ApplyPaging", parameterCount: 2, arity: 0);
        IMethodSymbol? applyOrderBy = GetMethod(specification, "ApplyOrderBy", parameterCount: 1, arity: 1);
        IMethodSymbol? applyOrderByDescending =
            GetMethod(specification, "ApplyOrderByDescending", parameterCount: 1, arity: 1);

        if (applyPaging is null || applyOrderBy is null || applyOrderByDescending is null)
        {
            return;
        }

        MethodSymbols methods = new(
            specification,
            applyPaging,
            applyOrderBy,
            applyOrderByDescending);

        context.RegisterSyntaxNodeAction(
            syntaxContext => AnalyzeInvocation(syntaxContext, methods),
            SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(
        SyntaxNodeAnalysisContext context,
        MethodSymbols methods)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        IMethodSymbol? targetMethod = GetInvokedMethod(context.SemanticModel, invocation, context.CancellationToken);

        if (targetMethod is null
            || !IsSameMethod(targetMethod, methods.ApplyPaging)
            || !IsCurrentInstanceInvocation(invocation, targetMethod))
        {
            return;
        }

        if (context.ContainingSymbol is not IMethodSymbol containingMethod
            || containingMethod.ContainingType is not INamedTypeSymbol containingType
            || !DerivesFromSpecification(containingType, methods.Specification))
        {
            return;
        }

        bool shouldReport = containingMethod.MethodKind switch
        {
            MethodKind.Constructor => ShouldReportConstructorPaging(
                containingMethod,
                context.SemanticModel,
                methods,
                context.CancellationToken),
            MethodKind.Ordinary => ShouldReportHelperPaging(
                containingMethod,
                context.SemanticModel,
                methods,
                context.CancellationToken),
            _ => false,
        };

        if (!shouldReport)
        {
            return;
        }

        context.ReportDiagnostic(
            Diagnostic.Create(
                Rule,
                GetInvocationNameLocation(invocation),
                containingType.Name));
    }

    private static bool ShouldReportConstructorPaging(
        IMethodSymbol constructor,
        SemanticModel semanticModel,
        MethodSymbols methods,
        CancellationToken cancellationToken)
        => TryAnalyzeLinearMethod(
                constructor,
                semanticModel,
                methods,
                ImmutableHashSet<IMethodSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default),
                cancellationToken,
                out MethodSummary summary)
            && !summary.HasPrimaryOrder;

    private static bool ShouldReportHelperPaging(
        IMethodSymbol helper,
        SemanticModel semanticModel,
        MethodSymbols methods,
        CancellationToken cancellationToken)
    {
        if (!IsEligibleInitializationHelper(helper)
            || !TryAnalyzeLinearMethod(
                helper,
                semanticModel,
                methods,
                ImmutableHashSet<IMethodSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default),
                cancellationToken,
                out MethodSummary helperSummary)
            || helperSummary.HasPrimaryOrder)
        {
            return false;
        }

        bool foundDirectConstructorCaller = false;

        foreach (IMethodSymbol constructor in helper.ContainingType.InstanceConstructors)
        {
            if (constructor.IsImplicitlyDeclared)
            {
                continue;
            }

            if (!TryAnalyzeLinearMethod(
                constructor,
                semanticModel,
                methods,
                ImmutableHashSet<IMethodSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default),
                cancellationToken,
                out MethodSummary constructorSummary))
            {
                return false;
            }

            if (!constructorSummary.DirectHelpers.Contains(helper.OriginalDefinition))
            {
                continue;
            }

            foundDirectConstructorCaller = true;

            if (constructorSummary.HasPrimaryOrder)
            {
                return false;
            }
        }

        return foundDirectConstructorCaller;
    }

    private static bool TryAnalyzeLinearMethod(
        IMethodSymbol method,
        SemanticModel semanticModel,
        MethodSymbols methods,
        ImmutableHashSet<IMethodSymbol> activeMethods,
        CancellationToken cancellationToken,
        out MethodSummary summary)
    {
        summary = MethodSummary.Empty;

        IMethodSymbol methodDefinition = method.OriginalDefinition;
        if (activeMethods.Contains(methodDefinition)
            || method.DeclaringSyntaxReferences.Length != 1)
        {
            return false;
        }

        SyntaxNode declaration = method.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken);
        if (declaration.SyntaxTree != semanticModel.SyntaxTree)
        {
            return false;
        }

        ImmutableHashSet<IMethodSymbol> nextActive = activeMethods.Add(methodDefinition);

        if (declaration is ConstructorDeclarationSyntax constructor)
        {
            if (!HasSafeConstructorBasePath(constructor, method, semanticModel, methods, cancellationToken))
            {
                return false;
            }

            return TryAnalyzeBody(
                constructor.Body,
                constructor.ExpressionBody,
                method,
                semanticModel,
                methods,
                nextActive,
                cancellationToken,
                out summary);
        }

        if (declaration is MethodDeclarationSyntax methodDeclaration)
        {
            if (!IsEligibleInitializationHelper(method))
            {
                return false;
            }

            return TryAnalyzeBody(
                methodDeclaration.Body,
                methodDeclaration.ExpressionBody,
                method,
                semanticModel,
                methods,
                nextActive,
                cancellationToken,
                out summary);
        }

        return false;
    }

    private static bool HasSafeConstructorBasePath(
        ConstructorDeclarationSyntax constructor,
        IMethodSymbol constructorSymbol,
        SemanticModel semanticModel,
        MethodSymbols methods,
        CancellationToken cancellationToken)
    {
        if (constructor.Initializer is { ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword })
        {
            return false;
        }

        if (constructor.Initializer is not null)
        {
            IMethodSymbol? initializerTarget = semanticModel.GetSymbolInfo(
                    constructor.Initializer,
                    cancellationToken)
                .Symbol as IMethodSymbol;

            return initializerTarget is not null
                && SymbolEqualityComparer.Default.Equals(
                    initializerTarget.ContainingType.OriginalDefinition,
                    methods.Specification);
        }

        INamedTypeSymbol? baseType = constructorSymbol.ContainingType.BaseType;
        return baseType is not null
            && SymbolEqualityComparer.Default.Equals(
                baseType.OriginalDefinition,
                methods.Specification);
    }

    private static bool TryAnalyzeBody(
        BlockSyntax? body,
        ArrowExpressionClauseSyntax? expressionBody,
        IMethodSymbol owningMethod,
        SemanticModel semanticModel,
        MethodSymbols methods,
        ImmutableHashSet<IMethodSymbol> activeMethods,
        CancellationToken cancellationToken,
        out MethodSummary summary)
    {
        summary = MethodSummary.Empty;

        if (body is not null)
        {
            foreach (StatementSyntax statement in body.Statements)
            {
                if (!TryAnalyzeStatement(
                    statement,
                    owningMethod,
                    semanticModel,
                    methods,
                    activeMethods,
                    cancellationToken,
                    ref summary))
                {
                    return false;
                }
            }

            return true;
        }

        if (expressionBody is not null)
        {
            return TryAnalyzeExpression(
                expressionBody.Expression,
                owningMethod,
                semanticModel,
                methods,
                activeMethods,
                cancellationToken,
                ref summary);
        }

        return false;
    }

    private static bool TryAnalyzeStatement(
        StatementSyntax statement,
        IMethodSymbol owningMethod,
        SemanticModel semanticModel,
        MethodSymbols methods,
        ImmutableHashSet<IMethodSymbol> activeMethods,
        CancellationToken cancellationToken,
        ref MethodSummary summary)
    {
        switch (statement)
        {
            case EmptyStatementSyntax:
                return true;

            case ExpressionStatementSyntax expressionStatement:
                return TryAnalyzeExpression(
                    expressionStatement.Expression,
                    owningMethod,
                    semanticModel,
                    methods,
                    activeMethods,
                    cancellationToken,
                    ref summary);

            case LocalDeclarationStatementSyntax localDeclaration:
                return !ContainsPotentialCurrentInstanceMutation(
                    localDeclaration,
                    semanticModel,
                    owningMethod.ContainingType,
                    methods,
                    cancellationToken);

            default:
                return false;
        }
    }

    private static bool TryAnalyzeExpression(
        ExpressionSyntax expression,
        IMethodSymbol owningMethod,
        SemanticModel semanticModel,
        MethodSymbols methods,
        ImmutableHashSet<IMethodSymbol> activeMethods,
        CancellationToken cancellationToken,
        ref MethodSummary summary)
    {
        if (expression is AwaitExpressionSyntax awaitExpression)
        {
            expression = awaitExpression.Expression;
        }

        if (expression is not InvocationExpressionSyntax invocation)
        {
            return !ContainsPotentialCurrentInstanceMutation(
                expression,
                semanticModel,
                owningMethod.ContainingType,
                methods,
                cancellationToken);
        }

        IMethodSymbol? targetMethod = GetInvokedMethod(semanticModel, invocation, cancellationToken);
        if (targetMethod is null)
        {
            return false;
        }

        if ((IsSameMethod(targetMethod, methods.ApplyOrderBy)
                || IsSameMethod(targetMethod, methods.ApplyOrderByDescending))
            && IsCurrentInstanceInvocation(invocation, targetMethod))
        {
            summary = summary.WithPrimaryOrder();
            return true;
        }

        if (SymbolEqualityComparer.Default.Equals(
            targetMethod.ContainingType.OriginalDefinition,
            methods.Specification))
        {
            return true;
        }

        if (IsCurrentInstanceInvocation(invocation, targetMethod))
        {
            if (!SymbolEqualityComparer.Default.Equals(
                    targetMethod.ContainingType,
                    owningMethod.ContainingType)
                || !IsEligibleInitializationHelper(targetMethod))
            {
                return false;
            }

            if (!TryAnalyzeLinearMethod(
                targetMethod,
                semanticModel,
                methods,
                activeMethods,
                cancellationToken,
                out MethodSummary helperSummary))
            {
                return false;
            }

            summary = summary
                .WithDirectHelper(targetMethod.OriginalDefinition)
                .MergePrimaryOrder(helperSummary);
            return true;
        }

        return !ContainsThisArgument(invocation);
    }

    private static bool ContainsPotentialCurrentInstanceMutation(
        SyntaxNode node,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        MethodSymbols methods,
        CancellationToken cancellationToken)
    {
        foreach (InvocationExpressionSyntax invocation in node.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Ancestors().Any(
                ancestor => ancestor != node
                    && ancestor is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            {
                continue;
            }

            IMethodSymbol? targetMethod = GetInvokedMethod(semanticModel, invocation, cancellationToken);
            if (targetMethod is null)
            {
                return true;
            }

            if (SymbolEqualityComparer.Default.Equals(
                targetMethod.ContainingType.OriginalDefinition,
                methods.Specification))
            {
                return true;
            }

            if (IsCurrentInstanceInvocation(invocation, targetMethod)
                && (SymbolEqualityComparer.Default.Equals(targetMethod.ContainingType, containingType)
                    || IsBaseTypeOf(targetMethod.ContainingType, containingType)))
            {
                return true;
            }

            if (ContainsThisArgument(invocation))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCurrentInstanceInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol targetMethod)
    {
        if (targetMethod.IsStatic && targetMethod.ReducedFrom is null)
        {
            return false;
        }

        return invocation.Expression switch
        {
            IdentifierNameSyntax => true,
            GenericNameSyntax => true,
            MemberAccessExpressionSyntax memberAccess =>
                IsThisOrBaseExpression(memberAccess.Expression),
            _ => false,
        };
    }

    private static bool IsThisOrBaseExpression(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesized)
        {
            expression = parenthesized.Expression;
        }

        return expression is ThisExpressionSyntax or BaseExpressionSyntax;
    }

    private static bool ContainsThisArgument(InvocationExpressionSyntax invocation)
        => invocation.ArgumentList.Arguments.Any(
            argument => argument.Expression.DescendantNodesAndSelf().OfType<ThisExpressionSyntax>().Any());

    private static bool IsEligibleInitializationHelper(IMethodSymbol method)
        => method.MethodKind == MethodKind.Ordinary
            && !method.IsStatic
            && !method.IsAsync
            && !method.IsAbstract
            && !method.IsVirtual
            && !method.IsOverride
            && !method.IsExtern;

    private static bool DerivesFromSpecification(
        INamedTypeSymbol type,
        INamedTypeSymbol specification)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(
                current.OriginalDefinition,
                specification))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBaseTypeOf(INamedTypeSymbol candidateBase, INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidateBase))
            {
                return true;
            }
        }

        return false;
    }

    private static IMethodSymbol? GetMethod(
        INamedTypeSymbol type,
        string name,
        int parameterCount,
        int arity)
    {
        ImmutableArray<IMethodSymbol> matches = type.GetMembers(name)
            .OfType<IMethodSymbol>()
            .Where(method => method.Parameters.Length == parameterCount && method.Arity == arity)
            .Take(2)
            .ToImmutableArray();

        return matches.Length == 1 ? matches[0] : null;
    }

    private static IMethodSymbol? GetInvokedMethod(
        SemanticModel semanticModel,
        InvocationExpressionSyntax invocation,
        CancellationToken cancellationToken)
    {
        SymbolInfo symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        if (symbolInfo.Symbol is IMethodSymbol method)
        {
            return method;
        }

        ImmutableArray<IMethodSymbol> candidates = symbolInfo.CandidateSymbols
            .OfType<IMethodSymbol>()
            .Take(2)
            .ToImmutableArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static bool IsSameMethod(IMethodSymbol candidate, IMethodSymbol expected)
        => SymbolEqualityComparer.Default.Equals(
            candidate.OriginalDefinition,
            expected.OriginalDefinition);

    private static Location GetInvocationNameLocation(InvocationExpressionSyntax invocation)
        => invocation.Expression switch
        {
            SimpleNameSyntax name => name.GetLocation(),
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.GetLocation(),
            _ => invocation.GetLocation(),
        };

    private readonly struct MethodSymbols
    {
        public MethodSymbols(
            INamedTypeSymbol specification,
            IMethodSymbol applyPaging,
            IMethodSymbol applyOrderBy,
            IMethodSymbol applyOrderByDescending)
        {
            Specification = specification;
            ApplyPaging = applyPaging;
            ApplyOrderBy = applyOrderBy;
            ApplyOrderByDescending = applyOrderByDescending;
        }

        public INamedTypeSymbol Specification { get; }

        public IMethodSymbol ApplyPaging { get; }

        public IMethodSymbol ApplyOrderBy { get; }

        public IMethodSymbol ApplyOrderByDescending { get; }
    }

    private readonly struct MethodSummary
    {
        private MethodSummary(
            bool hasPrimaryOrder,
            ImmutableHashSet<IMethodSymbol> directHelpers)
        {
            HasPrimaryOrder = hasPrimaryOrder;
            DirectHelpers = directHelpers;
        }

        public static MethodSummary Empty { get; } = new(
            hasPrimaryOrder: false,
            ImmutableHashSet<IMethodSymbol>.Empty.WithComparer(SymbolEqualityComparer.Default));

        public bool HasPrimaryOrder { get; }

        public ImmutableHashSet<IMethodSymbol> DirectHelpers { get; }

        public MethodSummary WithPrimaryOrder() =>
            new(hasPrimaryOrder: true, DirectHelpers);

        public MethodSummary WithDirectHelper(IMethodSymbol helper) =>
            new(HasPrimaryOrder, DirectHelpers.Add(helper));

        public MethodSummary MergePrimaryOrder(MethodSummary other) =>
            other.HasPrimaryOrder ? WithPrimaryOrder() : this;
    }
}
