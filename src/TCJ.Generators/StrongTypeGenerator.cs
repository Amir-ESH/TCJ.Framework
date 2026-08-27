using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TCJ.Generators;

[Generator]
public sealed class StrongTypeGenerator : IIncrementalGenerator
{
    private const string StrongIdAttribute = "TCJ.Core.StrongTypes.StronglyTypedIdAttribute`1";
    private const string ValueObjectAttribute = "TCJ.Core.StrongTypes.ValueObjectAttribute`1";
    private const string GuidTypeName = "global::System.Guid";
    private const string Int32TypeName = "global::System.Int32";
    private const string Int64TypeName = "global::System.Int64";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var strongIdCandidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            StrongIdAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (ctx, cancellationToken) => AnalyzeStrongId(ctx, cancellationToken));

        var supportedStrongIds = strongIdCandidates
            .Where(static candidate => candidate is not null && candidate.CanGenerate)
            .Select(static (candidate, _) => candidate!.Model!);

        context.RegisterSourceOutput(supportedStrongIds, static (spc, candidate) =>
        {
            spc.AddSource(candidate.HintName, GenerateStrongId(candidate));
        });

        var diagnosticCandidates = strongIdCandidates
            .Where(static candidate => candidate is not null && candidate.Diagnostics.Length > 0)
            .Select(static (candidate, _) => candidate!)
            .Collect();

        context.RegisterSourceOutput(diagnosticCandidates, static (spc, candidates) =>
        {
            foreach (var candidate in candidates
                .GroupBy(static candidate => candidate.SortKey, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => group.First()))
            {
                foreach (var diagnostic in candidate.Diagnostics
                    .OrderBy(static diagnostic => diagnostic.SourceTreeOrdinal)
                    .ThenBy(static diagnostic => diagnostic.Location.SourceSpan.Start)
                    .ThenBy(static diagnostic => diagnostic.Descriptor.Id, StringComparer.Ordinal)
                    .ThenBy(static diagnostic => diagnostic.MessageSortKey, StringComparer.Ordinal))
                {
                    spc.ReportDiagnostic(diagnostic.CreateDiagnostic());
                }
            }
        });

        var valueObjectCandidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            ValueObjectAttribute,
            static (node, _) => node is RecordDeclarationSyntax or StructDeclarationSyntax or ClassDeclarationSyntax,
            static (ctx, _) => ctx.TargetSymbol.ToDisplayString())
            .Collect();

        context.RegisterSourceOutput(valueObjectCandidates, static (spc, candidates) =>
        {
            foreach (var symbol in candidates.OrderBy(static x => x, StringComparer.Ordinal))
            {
                _ = symbol;
            }
        });
    }

    private static StrongIdCandidate? AnalyzeStrongId(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol || context.TargetNode is not TypeDeclarationSyntax declaration)
        {
            return null;
        }

        var diagnostics = new List<StrongIdDiagnostic>();
        var declarationLocation = declaration.Identifier.GetLocation();
        var compilation = context.SemanticModel.Compilation;
        var strongIdAttributes = symbol.GetAttributes()
            .Where(static attribute => IsAttribute(attribute, StrongIdAttribute))
            .ToArray();
        var valueObjectAttributes = symbol.GetAttributes()
            .Where(static attribute => IsAttribute(attribute, ValueObjectAttribute))
            .ToArray();

        if (strongIdAttributes.Length != 1 || valueObjectAttributes.Length != 0)
        {
            foreach (var attribute in strongIdAttributes.Concat(valueObjectAttributes)
                .OrderBy(static attribute => attribute.ApplicationSyntaxReference?.Span.Start ?? int.MaxValue))
            {
                var attributeName = attribute.AttributeClass?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    ?? "unknown";
                diagnostics.Add(new StrongIdDiagnostic(
                    compilation,
                    StrongTypeDiagnostics.AmbiguousAttributes,
                    GetAttributeLocation(attribute, declarationLocation, cancellationToken),
                    attributeName,
                    symbol.Name));
            }

            return new StrongIdCandidate(
                symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                null,
                diagnostics.ToImmutableArray());
        }

        var strongIdAttribute = strongIdAttributes[0];
        if (strongIdAttribute?.AttributeClass is null || strongIdAttribute.AttributeClass.TypeArguments.Length != 1)
        {
            return new StrongIdCandidate(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), null, diagnostics.ToImmutableArray());
        }

        var backingTypeSymbol = strongIdAttribute.AttributeClass.TypeArguments[0];
        var backingType = backingTypeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var backingKind = backingTypeSymbol.SpecialType switch
        {
            SpecialType.System_Int32 => StrongIdBackingKind.Int32,
            SpecialType.System_Int64 => StrongIdBackingKind.Int64,
            _ when string.Equals(backingType, GuidTypeName, StringComparison.Ordinal) => StrongIdBackingKind.Guid,
            _ => StrongIdBackingKind.Unsupported
        };

        var isPartial = declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
        var isReadOnly = declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.ReadOnlyKeyword));
        var isRecordStruct = declaration is RecordDeclarationSyntax recordDeclaration
            && recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword);
        var isTopLevel = symbol.ContainingType is null;
        var isNonGeneric = symbol.TypeParameters.Length == 0;
        var isFileLocal = declaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.FileKeyword));
        var isSupportedAccessibility = !isFileLocal
            && symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

        if (!isPartial)
        {
            diagnostics.Add(new StrongIdDiagnostic(
                compilation,
                StrongTypeDiagnostics.NonPartial,
                declarationLocation,
                symbol.Name));
        }

        if (!isRecordStruct || !isReadOnly || !isTopLevel || !isSupportedAccessibility)
        {
            diagnostics.Add(new StrongIdDiagnostic(
                compilation,
                StrongTypeDiagnostics.UnsupportedShape,
                declarationLocation,
                symbol.Name));
        }

        if (!isNonGeneric)
        {
            diagnostics.Add(new StrongIdDiagnostic(
                compilation,
                StrongTypeDiagnostics.GenericDeclaration,
                declarationLocation,
                symbol.Name));
        }

        if (backingKind == StrongIdBackingKind.Unsupported)
        {
            diagnostics.Add(new StrongIdDiagnostic(
                compilation,
                StrongTypeDiagnostics.UnsupportedBackingType,
                GetAttributeLocation(strongIdAttribute, declarationLocation, cancellationToken),
                symbol.Name,
                backingTypeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        if (backingKind != StrongIdBackingKind.Unsupported && isRecordStruct && isTopLevel && isNonGeneric && isSupportedAccessibility)
        {
            AddGeneratedMemberCollisionDiagnostics(
                diagnostics,
                symbol,
                backingTypeSymbol,
                backingKind,
                compilation);
        }

        var namespaceName = symbol.ContainingNamespace.IsGlobalNamespace
            ? null
            : symbol.ContainingNamespace.ToDisplayString();
        var accessibility = symbol.DeclaredAccessibility == Accessibility.Public ? "public" : "internal";
        var typeName = EscapeIdentifier(symbol.Name);
        var qualifiedName = namespaceName is null ? symbol.Name : namespaceName + "." + symbol.Name;
        var model = new StrongIdModel(
            namespaceName,
            accessibility,
            typeName,
            $"TCJ.StronglyTypedId.{qualifiedName}.g.cs",
            backingKind);

        return new StrongIdCandidate(
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            model,
            diagnostics.ToImmutableArray());
    }

    private static bool IsAttribute(AttributeData attribute, string metadataName)
    {
        var attributeClass = attribute.AttributeClass?.OriginalDefinition;
        if (attributeClass is null)
        {
            return false;
        }

        var namespaceName = attributeClass.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : attributeClass.ContainingNamespace.ToDisplayString() + ".";
        return string.Equals(namespaceName + attributeClass.MetadataName, metadataName, StringComparison.Ordinal);
    }

    private static Location GetAttributeLocation(
        AttributeData attribute,
        Location fallback,
        CancellationToken cancellationToken)
    {
        return attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation() ?? fallback;
    }

    private static void AddGeneratedMemberCollisionDiagnostics(
        List<StrongIdDiagnostic> diagnostics,
        INamedTypeSymbol strongId,
        ITypeSymbol backingType,
        StrongIdBackingKind backingKind,
        Compilation compilation)
    {
        foreach (var member in strongId.GetMembers()
            .Where(static member => !member.IsImplicitlyDeclared && member.Locations.Any(static location => location.IsInSource))
            .OrderBy(static member => member.Locations.First(static location => location.IsInSource).SourceSpan.Start)
            .ThenBy(static member => member.Name, StringComparer.Ordinal))
        {
            if (!ConflictsWithGeneratedApi(member, strongId, backingType, backingKind, compilation))
            {
                continue;
            }

            diagnostics.Add(new StrongIdDiagnostic(
                compilation,
                StrongTypeDiagnostics.GeneratedMemberCollision,
                member.Locations.First(static location => location.IsInSource),
                member.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                strongId.Name));
        }
    }

    private static bool ConflictsWithGeneratedApi(
        ISymbol member,
        INamedTypeSymbol strongId,
        ITypeSymbol backingType,
        StrongIdBackingKind backingKind,
        Compilation compilation)
    {
        if (member.Name is "Value" or "IsDefault" or "StrongIdConversion" or "StrongIdJsonConverter")
        {
            return true;
        }

        if (member is not IMethodSymbol method)
        {
            return member.Name is "Parse" or "TryParse" or "ToString" or "TryFormat"
                || (backingKind == StrongIdBackingKind.Guid
                    && (string.Equals(member.Name, "New", StringComparison.Ordinal)
                        || string.Equals(member.Name, "NewVersion7", StringComparison.Ordinal)));
        }

        if (method.MethodKind == MethodKind.Constructor)
        {
            return MatchesParameters(method, new[] { backingType }, new[] { RefKind.None });
        }

        if (method.MethodKind == MethodKind.Conversion && method.Parameters.Length == 1)
        {
            return (AreSameType(method.Parameters[0].Type, backingType) && AreSameType(method.ReturnType, strongId))
                || (AreSameType(method.Parameters[0].Type, strongId) && AreSameType(method.ReturnType, backingType));
        }

        if (method.MethodKind != MethodKind.Ordinary || method.Arity != 0)
        {
            return false;
        }

        var stringType = compilation.GetSpecialType(SpecialType.System_String);
        var charType = compilation.GetSpecialType(SpecialType.System_Char);
        var intType = compilation.GetSpecialType(SpecialType.System_Int32);
        var formatProviderType = compilation.GetTypeByMetadataName("System.IFormatProvider");
        var readOnlySpanDefinition = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
        var spanDefinition = compilation.GetTypeByMetadataName("System.Span`1");
        var readOnlySpanOfChar = readOnlySpanDefinition?.Construct(charType);
        var spanOfChar = spanDefinition?.Construct(charType);

        if (MatchesMethod(method, "Parse", new ITypeSymbol?[] { stringType }, new[] { RefKind.None })
            || MatchesMethod(method, "Parse", new ITypeSymbol?[] { stringType, formatProviderType }, new[] { RefKind.None, RefKind.None })
            || MatchesMethod(method, "Parse", new ITypeSymbol?[] { readOnlySpanOfChar }, new[] { RefKind.None })
            || MatchesMethod(method, "Parse", new ITypeSymbol?[] { readOnlySpanOfChar, formatProviderType }, new[] { RefKind.None, RefKind.None })
            || MatchesMethod(method, "TryParse", new ITypeSymbol?[] { stringType, strongId }, new[] { RefKind.None, RefKind.Out })
            || MatchesMethod(method, "TryParse", new ITypeSymbol?[] { stringType, formatProviderType, strongId }, new[] { RefKind.None, RefKind.None, RefKind.Out })
            || MatchesMethod(method, "TryParse", new ITypeSymbol?[] { readOnlySpanOfChar, strongId }, new[] { RefKind.None, RefKind.Out })
            || MatchesMethod(method, "TryParse", new ITypeSymbol?[] { readOnlySpanOfChar, formatProviderType, strongId }, new[] { RefKind.None, RefKind.None, RefKind.Out })
            || MatchesMethod(method, "ToString", Array.Empty<ITypeSymbol?>(), Array.Empty<RefKind>())
            || MatchesMethod(method, "ToString", new ITypeSymbol?[] { stringType, formatProviderType }, new[] { RefKind.None, RefKind.None })
            || MatchesMethod(method, "TryFormat", new ITypeSymbol?[] { spanOfChar, intType, readOnlySpanOfChar, formatProviderType }, new[] { RefKind.None, RefKind.Out, RefKind.None, RefKind.None }))
        {
            return true;
        }

        if (backingKind != StrongIdBackingKind.Guid)
        {
            return false;
        }

        var guidGeneratorType = compilation.GetTypeByMetadataName("TCJ.Core.Identifiers.IGuidGenerator");
        return MatchesMethod(method, "New", new ITypeSymbol?[] { guidGeneratorType }, new[] { RefKind.None })
            || MatchesMethod(method, "NewVersion7", new ITypeSymbol?[] { guidGeneratorType }, new[] { RefKind.None });
    }

    private static bool MatchesMethod(
        IMethodSymbol method,
        string name,
        ITypeSymbol?[] parameterTypes,
        RefKind[] refKinds)
    {
        return string.Equals(method.Name, name, StringComparison.Ordinal)
            && MatchesParameters(method, parameterTypes, refKinds);
    }

    private static bool MatchesParameters(
        IMethodSymbol method,
        ITypeSymbol?[] parameterTypes,
        RefKind[] refKinds)
    {
        if (method.Parameters.Length != parameterTypes.Length || parameterTypes.Length != refKinds.Length)
        {
            return false;
        }

        for (var index = 0; index < parameterTypes.Length; index++)
        {
            if (parameterTypes[index] is null
                || !AreEquivalentParameterRefKinds(method.Parameters[index].RefKind, refKinds[index])
                || !AreSameType(method.Parameters[index].Type, parameterTypes[index]!))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreEquivalentParameterRefKinds(RefKind userRefKind, RefKind generatedRefKind)
    {
        if (generatedRefKind == RefKind.None)
        {
            return userRefKind == RefKind.None;
        }

        return userRefKind != RefKind.None;
    }

    private static bool AreSameType(ITypeSymbol left, ITypeSymbol right)
    {
        return SymbolEqualityComparer.Default.Equals(left, right);
    }

    private static string GenerateStrongId(StrongIdModel model)
    {
        if (model.BackingKind == StrongIdBackingKind.Guid)
        {
            return GenerateGuidStrongId(model);
        }

        var backingType = model.BackingKind == StrongIdBackingKind.Int32 ? Int32TypeName : Int64TypeName;
        var description = model.BackingKind == StrongIdBackingKind.Int32 ? "32-bit integer" : "64-bit integer";
        return GenerateNumericStrongId(model, backingType, description);
    }

    private static string GenerateGuidStrongId(StrongIdModel model)
    {
        var source = new StringBuilder();
        AppendLine(source, "// <auto-generated/>");
        AppendLine(source, "#nullable enable");
        AppendLine(source);

        if (model.NamespaceName is not null)
        {
            source.Append("namespace ").Append(model.NamespaceName);
            AppendLine(source);
            AppendLine(source, "{");
        }

        var indent = model.NamespaceName is null ? string.Empty : "    ";
        var memberIndent = indent + "    ";
        var bodyIndent = memberIndent + "    ";

        AppendLine(source, indent + "/// <summary>");
        AppendLine(source, indent + "/// Represents a strongly typed identifier backed by a GUID value.");
        AppendLine(source, indent + "/// </summary>");
        source.Append(indent)
            .Append("[global::System.Text.Json.Serialization.JsonConverter(typeof(")
            .Append(model.TypeName)
            .Append(".StrongIdJsonConverter))]");
        AppendLine(source);
        source.Append(indent)
            .Append(model.Accessibility)
            .Append(" readonly partial record struct ")
            .Append(model.TypeName)
            .Append(" : global::System.IParsable<")
            .Append(model.TypeName)
            .Append(">, global::System.ISpanParsable<")
            .Append(model.TypeName)
            .Append(">, global::System.IFormattable, global::System.ISpanFormattable");
        AppendLine(source);
        AppendLine(source, indent + "{");

        AppendLine(source, memberIndent + "/// <summary>");
        source.Append(memberIndent).Append("/// Initializes a new instance of the <see cref=\"")
            .Append(model.TypeName)
            .Append("\"/> struct.");
        AppendLine(source);
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"value\">The underlying identifier value.</param>");
        source.Append(memberIndent).Append("public ").Append(model.TypeName).Append("(global::System.Guid value)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "Value = value;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Gets the underlying identifier value.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public global::System.Guid Value { get; }");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Gets a value indicating whether this identifier contains the default GUID value.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public bool IsDefault => Value == global::System.Guid.Empty;");
        AppendLine(source);
        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Creates a new strongly typed identifier from a version 4 GUID produced by the supplied generator.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"generator\">The GUID generator used to create the underlying value.</param>");
        AppendLine(source, memberIndent + "/// <returns>A strongly typed identifier containing the exact generated GUID.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" New(global::TCJ.Core.Identifiers.IGuidGenerator generator)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "global::System.ArgumentNullException.ThrowIfNull(generator);");
        source.Append(bodyIndent).Append("return new ").Append(model.TypeName).Append("(generator.Create());");
        AppendLine(source);
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Creates a new strongly typed identifier from a version 7 GUID produced by the supplied generator.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"generator\">The GUID generator used to create the underlying value.</param>");
        AppendLine(source, memberIndent + "/// <returns>A strongly typed identifier containing the exact generated GUID.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" NewVersion7(global::TCJ.Core.Identifiers.IGuidGenerator generator)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "global::System.ArgumentNullException.ThrowIfNull(generator);");
        source.Append(bodyIndent).Append("return new ").Append(model.TypeName).Append("(generator.CreateVersion7());");
        AppendLine(source);
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses a canonical GUID D-format string into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format text to parse.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(string s) => new(global::System.Guid.ParseExact(s, \"D\"));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses a canonical GUID D-format string into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses culture-stable GUID semantics.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(string s, global::System.IFormatProvider? provider) => new(global::System.Guid.ParseExact(s, \"D\"));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses canonical GUID D-format characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(global::System.ReadOnlySpan<char> s) => new(global::System.Guid.ParseExact(s, \"D\"));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses canonical GUID D-format characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses culture-stable GUID semantics.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(global::System.ReadOnlySpan<char> s, global::System.IFormatProvider? provider) => new(global::System.Guid.ParseExact(s, \"D\"));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse canonical GUID D-format text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(string? s, out ").Append(model.TypeName)
            .Append(" result) => TryParse(s, null, out result);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse canonical GUID D-format text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses culture-stable GUID semantics.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(string? s, global::System.IFormatProvider? provider, out ")
            .Append(model.TypeName).Append(" result)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "if (global::System.Guid.TryParseExact(s, \"D\", out var value))");
        AppendLine(source, bodyIndent + "{");
        source.Append(bodyIndent).Append("    result = new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "    return true;");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "result = default;");
        AppendLine(source, bodyIndent + "return false;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse canonical GUID D-format characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(global::System.ReadOnlySpan<char> s, out ").Append(model.TypeName)
            .Append(" result) => TryParse(s, null, out result);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse canonical GUID D-format characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The canonical GUID D-format characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses culture-stable GUID semantics.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(global::System.ReadOnlySpan<char> s, global::System.IFormatProvider? provider, out ")
            .Append(model.TypeName).Append(" result)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "if (global::System.Guid.TryParseExact(s, \"D\", out var value))");
        AppendLine(source, bodyIndent + "{");
        source.Append(bodyIndent).Append("    result = new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "    return true;");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "result = default;");
        AppendLine(source, bodyIndent + "return false;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Returns the identifier value in the canonical GUID D format.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <returns>The canonical textual representation of the underlying GUID.</returns>");
        AppendLine(source, memberIndent + "public override string ToString() => Value.ToString(\"D\", global::System.Globalization.CultureInfo.InvariantCulture);");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Formats the identifier using a GUID format specifier and culture-stable semantics.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"format\">A GUID format specifier. Null or empty uses the canonical D format.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"formatProvider\">Ignored. Formatting always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <returns>The formatted identifier.</returns>");
        AppendLine(source, memberIndent + "public string ToString(string? format, global::System.IFormatProvider? formatProvider) =>");
        AppendLine(source, bodyIndent + "Value.ToString(global::System.String.IsNullOrEmpty(format) ? \"D\" : format, global::System.Globalization.CultureInfo.InvariantCulture);");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to format the identifier into the supplied character span without allocating a string.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"destination\">The span that receives the formatted identifier.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"charsWritten\">When successful, receives the number of characters written.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"format\">A GUID format specifier. Empty uses the canonical D format.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Formatting always uses culture-stable GUID semantics.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> if the destination was large enough; otherwise, <see langword=\"false\"/>.</returns>");
        AppendLine(source, memberIndent + "public bool TryFormat(global::System.Span<char> destination, out int charsWritten, global::System.ReadOnlySpan<char> format = default, global::System.IFormatProvider? provider = null)");
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "if (format.IsEmpty)");
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, bodyIndent + "    format = \"D\";");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "return Value.TryFormat(destination, out charsWritten, format);");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Explicitly converts a GUID value to the strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"value\">The underlying GUID value.</param>");
        AppendLine(source, memberIndent + "/// <returns>A strongly typed identifier containing the exact GUID value.</returns>");
        source.Append(memberIndent).Append("public static explicit operator ").Append(model.TypeName)
            .Append("(global::System.Guid value) => new(value);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Explicitly converts the strongly typed identifier to its underlying GUID value.");
        AppendLine(source, memberIndent + "/// </summary>");
        source.Append(memberIndent).Append("/// <param name=\"value\">The strongly typed identifier.</param>");
        AppendLine(source);
        AppendLine(source, memberIndent + "/// <returns>The exact underlying GUID value.</returns>");
        source.Append(memberIndent).Append("public static explicit operator global::System.Guid(").Append(model.TypeName)
            .Append(" value) => value.Value;");
        AppendLine(source);
        AppendLine(source);

        AppendStrongIdConversion(source, model, GuidTypeName, memberIndent, bodyIndent);
        AppendLine(source);
        AppendGuidJsonConverter(source, model, memberIndent, bodyIndent);

        AppendLine(source, indent + "}");

        if (model.NamespaceName is not null)
        {
            AppendLine(source, "}");
        }

        return source.ToString();
    }

    private static string GenerateNumericStrongId(StrongIdModel model, string backingType, string backingDescription)
    {
        var source = new StringBuilder();
        AppendLine(source, "// <auto-generated/>");
        AppendLine(source, "#nullable enable");
        AppendLine(source);

        if (model.NamespaceName is not null)
        {
            source.Append("namespace ").Append(model.NamespaceName);
            AppendLine(source);
            AppendLine(source, "{");
        }

        var indent = model.NamespaceName is null ? string.Empty : "    ";
        var memberIndent = indent + "    ";
        var bodyIndent = memberIndent + "    ";

        AppendLine(source, indent + "/// <summary>");
        source.Append(indent).Append("/// Represents a strongly typed identifier backed by a ").Append(backingDescription).Append(" value.");
        AppendLine(source);
        AppendLine(source, indent + "/// </summary>");
        source.Append(indent)
            .Append("[global::System.Text.Json.Serialization.JsonConverter(typeof(")
            .Append(model.TypeName)
            .Append(".StrongIdJsonConverter))]");
        AppendLine(source);
        source.Append(indent)
            .Append(model.Accessibility)
            .Append(" readonly partial record struct ")
            .Append(model.TypeName)
            .Append(" : global::System.IParsable<")
            .Append(model.TypeName)
            .Append(">, global::System.ISpanParsable<")
            .Append(model.TypeName)
            .Append(">, global::System.IFormattable, global::System.ISpanFormattable");
        AppendLine(source);
        AppendLine(source, indent + "{");

        AppendLine(source, memberIndent + "/// <summary>");
        source.Append(memberIndent).Append("/// Initializes a new instance of the <see cref=\"")
            .Append(model.TypeName)
            .Append("\"/> struct.");
        AppendLine(source);
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"value\">The underlying identifier value.</param>");
        source.Append(memberIndent).Append("public ").Append(model.TypeName).Append("(").Append(backingType).Append(" value)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "Value = value;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Gets the underlying identifier value.");
        AppendLine(source, memberIndent + "/// </summary>");
        source.Append(memberIndent).Append("public ").Append(backingType).Append(" Value { get; }");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Gets a value indicating whether this identifier contains the default numeric value.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public bool IsDefault => Value == default;");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses invariant base-10 integer text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer text to parse.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName).Append(" Parse(string s) => new(")
            .Append(backingType)
            .Append(".Parse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses invariant base-10 integer text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(string s, global::System.IFormatProvider? provider) => Parse(s);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses invariant base-10 integer characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(global::System.ReadOnlySpan<char> s) => new(")
            .Append(backingType)
            .Append(".Parse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture));");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Parses invariant base-10 integer characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <returns>The parsed strongly typed identifier.</returns>");
        source.Append(memberIndent).Append("public static ").Append(model.TypeName)
            .Append(" Parse(global::System.ReadOnlySpan<char> s, global::System.IFormatProvider? provider) => Parse(s);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse invariant base-10 integer text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(string? s, out ").Append(model.TypeName)
            .Append(" result) => TryParse(s, null, out result);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse invariant base-10 integer text into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer text to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(string? s, global::System.IFormatProvider? provider, out ")
            .Append(model.TypeName).Append(" result)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        source.Append(bodyIndent).Append("if (").Append(backingType)
            .Append(".TryParse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var value))");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        source.Append(bodyIndent).Append("    result = new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "    return true;");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "result = default;");
        AppendLine(source, bodyIndent + "return false;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse invariant base-10 integer characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(global::System.ReadOnlySpan<char> s, out ").Append(model.TypeName)
            .Append(" result) => TryParse(s, null, out result);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to parse invariant base-10 integer characters into a strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"s\">The invariant integer characters to parse.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Parsing always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"result\">When successful, receives the parsed identifier; otherwise, the default identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> when parsing succeeds; otherwise, <see langword=\"false\"/>.</returns>");
        source.Append(memberIndent).Append("public static bool TryParse(global::System.ReadOnlySpan<char> s, global::System.IFormatProvider? provider, out ")
            .Append(model.TypeName).Append(" result)");
        AppendLine(source);
        AppendLine(source, memberIndent + "{");
        source.Append(bodyIndent).Append("if (").Append(backingType)
            .Append(".TryParse(s, global::System.Globalization.NumberStyles.Integer, global::System.Globalization.CultureInfo.InvariantCulture, out var value))");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        source.Append(bodyIndent).Append("    result = new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "    return true;");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "result = default;");
        AppendLine(source, bodyIndent + "return false;");
        AppendLine(source, memberIndent + "}");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Returns the identifier using invariant base-10 integer formatting.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <returns>The invariant textual representation of the underlying integer.</returns>");
        AppendLine(source, memberIndent + "public override string ToString() => Value.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture);");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Formats the identifier using invariant numeric formatting semantics.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"format\">A standard or custom numeric format string. Null or empty uses the default integer representation.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"formatProvider\">Ignored. Formatting always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <returns>The formatted identifier.</returns>");
        AppendLine(source, memberIndent + "public string ToString(string? format, global::System.IFormatProvider? formatProvider) =>");
        AppendLine(source, bodyIndent + "Value.ToString(format, global::System.Globalization.CultureInfo.InvariantCulture);");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Tries to format the identifier into the supplied character span without allocating a string.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"destination\">The span that receives the formatted identifier.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"charsWritten\">When successful, receives the number of characters written.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"format\">A standard or custom numeric format. Empty uses the default integer representation.</param>");
        AppendLine(source, memberIndent + "/// <param name=\"provider\">Ignored. Formatting always uses invariant culture.</param>");
        AppendLine(source, memberIndent + "/// <returns><see langword=\"true\"/> if the destination was large enough; otherwise, <see langword=\"false\"/>.</returns>");
        AppendLine(source, memberIndent + "public bool TryFormat(global::System.Span<char> destination, out int charsWritten, global::System.ReadOnlySpan<char> format = default, global::System.IFormatProvider? provider = null) =>");
        AppendLine(source, bodyIndent + "Value.TryFormat(destination, out charsWritten, format, global::System.Globalization.CultureInfo.InvariantCulture);");
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Explicitly converts an underlying numeric value to the strongly typed identifier.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"value\">The underlying numeric value.</param>");
        AppendLine(source, memberIndent + "/// <returns>A strongly typed identifier containing the exact numeric value.</returns>");
        source.Append(memberIndent).Append("public static explicit operator ").Append(model.TypeName)
            .Append("(").Append(backingType).Append(" value) => new(value);");
        AppendLine(source);
        AppendLine(source);

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Explicitly converts the strongly typed identifier to its underlying numeric value.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "/// <param name=\"value\">The strongly typed identifier.</param>");
        AppendLine(source, memberIndent + "/// <returns>The exact underlying numeric value.</returns>");
        source.Append(memberIndent).Append("public static explicit operator ").Append(backingType).Append("(").Append(model.TypeName)
            .Append(" value) => value.Value;");
        AppendLine(source);
        AppendLine(source);

        AppendStrongIdConversion(source, model, backingType, memberIndent, bodyIndent);
        AppendLine(source);
        AppendNumericJsonConverter(source, model, memberIndent, bodyIndent);

        AppendLine(source, indent + "}");

        if (model.NamespaceName is not null)
        {
            AppendLine(source, "}");
        }

        return source.ToString();
    }


    private static void AppendStrongIdConversion(
        StringBuilder source,
        StrongIdModel model,
        string backingType,
        string memberIndent,
        string bodyIndent)
    {
        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Provides provider-neutral conversion expressions between this strongly typed identifier and its backing value.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public static class StrongIdConversion");
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "/// <summary>");
        AppendLine(source, bodyIndent + "/// Gets the conversion expression from the strongly typed identifier to its backing value.");
        AppendLine(source, bodyIndent + "/// </summary>");
        source.Append(bodyIndent)
            .Append("public static global::System.Linq.Expressions.Expression<global::System.Func<")
            .Append(model.TypeName)
            .Append(", ")
            .Append(backingType)
            .Append(">> ToBackingValue { get; } = static value => value.Value;");
        AppendLine(source);
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <summary>");
        AppendLine(source, bodyIndent + "/// Gets the conversion expression from the backing value to the strongly typed identifier.");
        AppendLine(source, bodyIndent + "/// </summary>");
        source.Append(bodyIndent)
            .Append("public static global::System.Linq.Expressions.Expression<global::System.Func<")
            .Append(backingType)
            .Append(", ")
            .Append(model.TypeName)
            .Append(">> FromBackingValue { get; } = static value => new ")
            .Append(model.TypeName)
            .Append("(value);");
        AppendLine(source);
        AppendLine(source, memberIndent + "}");
    }

    private static void AppendGuidJsonConverter(
        StringBuilder source,
        StrongIdModel model,
        string memberIndent,
        string bodyIndent)
    {
        var converterBodyIndent = bodyIndent + "    ";

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Converts this strongly typed identifier to and from its scalar System.Text.Json representation.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public sealed class StrongIdJsonConverter : global::System.Text.Json.Serialization.JsonConverter<" + model.TypeName + ">");
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "/// <summary>");
        AppendLine(source, bodyIndent + "/// Initializes a new instance of the generated Strong ID JSON converter.");
        AppendLine(source, bodyIndent + "/// </summary>");
        AppendLine(source, bodyIndent + "public StrongIdJsonConverter()");
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        AppendLine(source, bodyIndent + "public override bool HandleNull => true;");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        source.Append(bodyIndent).Append("public override ").Append(model.TypeName)
            .Append(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, converterBodyIndent + "if (reader.TokenType != global::System.Text.Json.JsonTokenType.String)");
        AppendLine(source, converterBodyIndent + "{");
        source.Append(converterBodyIndent).Append("    throw new global::System.Text.Json.JsonException(\"Expected a JSON string token for ")
            .Append(model.TypeName).Append(".\");");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "}");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "if (!reader.TryGetGuid(out var value))");
        AppendLine(source, converterBodyIndent + "{");
        source.Append(converterBodyIndent).Append("    throw new global::System.Text.Json.JsonException(\"Invalid GUID value for ")
            .Append(model.TypeName).Append(".\");");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "}");
        AppendLine(source);
        source.Append(converterBodyIndent).Append("return new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        source.Append(bodyIndent).Append("public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(model.TypeName).Append(" value, global::System.Text.Json.JsonSerializerOptions options)");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, converterBodyIndent + "writer.WriteStringValue(value.Value);");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source, memberIndent + "}");
    }

    private static void AppendNumericJsonConverter(
        StringBuilder source,
        StrongIdModel model,
        string memberIndent,
        string bodyIndent)
    {
        var converterBodyIndent = bodyIndent + "    ";
        var tryGetMethod = model.BackingKind == StrongIdBackingKind.Int32 ? "TryGetInt32" : "TryGetInt64";

        AppendLine(source, memberIndent + "/// <summary>");
        AppendLine(source, memberIndent + "/// Converts this strongly typed identifier to and from its scalar System.Text.Json representation.");
        AppendLine(source, memberIndent + "/// </summary>");
        AppendLine(source, memberIndent + "public sealed class StrongIdJsonConverter : global::System.Text.Json.Serialization.JsonConverter<" + model.TypeName + ">");
        AppendLine(source, memberIndent + "{");
        AppendLine(source, bodyIndent + "/// <summary>");
        AppendLine(source, bodyIndent + "/// Initializes a new instance of the generated Strong ID JSON converter.");
        AppendLine(source, bodyIndent + "/// </summary>");
        AppendLine(source, bodyIndent + "public StrongIdJsonConverter()");
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        AppendLine(source, bodyIndent + "public override bool HandleNull => true;");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        source.Append(bodyIndent).Append("public override ").Append(model.TypeName)
            .Append(" Read(ref global::System.Text.Json.Utf8JsonReader reader, global::System.Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, converterBodyIndent + "if (reader.TokenType != global::System.Text.Json.JsonTokenType.Number)");
        AppendLine(source, converterBodyIndent + "{");
        source.Append(converterBodyIndent).Append("    throw new global::System.Text.Json.JsonException(\"Expected a JSON number token for ")
            .Append(model.TypeName).Append(".\");");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "}");
        AppendLine(source);
        source.Append(converterBodyIndent).Append("if (!reader.").Append(tryGetMethod).Append("(out var value))");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "{");
        source.Append(converterBodyIndent).Append("    throw new global::System.Text.Json.JsonException(\"Invalid numeric value for ")
            .Append(model.TypeName).Append(".\");");
        AppendLine(source);
        AppendLine(source, converterBodyIndent + "}");
        AppendLine(source);
        source.Append(converterBodyIndent).Append("return new ").Append(model.TypeName).Append("(value);");
        AppendLine(source);
        AppendLine(source, bodyIndent + "}");
        AppendLine(source);
        AppendLine(source, bodyIndent + "/// <inheritdoc />");
        source.Append(bodyIndent).Append("public override void Write(global::System.Text.Json.Utf8JsonWriter writer, ")
            .Append(model.TypeName).Append(" value, global::System.Text.Json.JsonSerializerOptions options)");
        AppendLine(source);
        AppendLine(source, bodyIndent + "{");
        AppendLine(source, converterBodyIndent + "writer.WriteNumberValue(value.Value);");
        AppendLine(source, bodyIndent + "}");
        AppendLine(source, memberIndent + "}");
    }


    private static void AppendLine(StringBuilder builder, string value = "")
    {
        builder.Append(value).Append('\n');
    }

    private static string EscapeIdentifier(string identifier)
    {
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ||
               SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None
            ? "@" + identifier
            : identifier;
    }

    private sealed class StrongIdCandidate
    {
        public StrongIdCandidate(
            string sortKey,
            StrongIdModel? model,
            ImmutableArray<StrongIdDiagnostic> diagnostics)
        {
            SortKey = sortKey;
            Model = model;
            Diagnostics = diagnostics;
        }

        public string SortKey { get; }

        public StrongIdModel? Model { get; }

        public ImmutableArray<StrongIdDiagnostic> Diagnostics { get; }

        public bool CanGenerate => Model is not null && Diagnostics.Length == 0;
    }

    private sealed class StrongIdDiagnostic
    {
        private readonly object[] _messageArguments;

        public StrongIdDiagnostic(
            Compilation compilation,
            DiagnosticDescriptor descriptor,
            Location location,
            params object[] messageArguments)
        {
            Descriptor = descriptor;
            Location = location;
            SourceTreeOrdinal = GetSourceTreeOrdinal(compilation, location.SourceTree);
            _messageArguments = messageArguments;
            MessageSortKey = string.Join("|", messageArguments.Select(static argument => argument?.ToString() ?? string.Empty));
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Location Location { get; }

        public int SourceTreeOrdinal { get; }

        public string MessageSortKey { get; }

        private static int GetSourceTreeOrdinal(Compilation compilation, SyntaxTree? sourceTree)
        {
            if (sourceTree is null)
            {
                return int.MaxValue;
            }

            var ordinal = 0;
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (ReferenceEquals(syntaxTree, sourceTree))
                {
                    return ordinal;
                }

                ordinal++;
            }

            return int.MaxValue;
        }

        public Diagnostic CreateDiagnostic()
        {
            return Diagnostic.Create(Descriptor, Location, _messageArguments);
        }
    }

    private sealed class StrongIdModel : IEquatable<StrongIdModel>
    {
        public StrongIdModel(
            string? namespaceName,
            string accessibility,
            string typeName,
            string hintName,
            StrongIdBackingKind backingKind)
        {
            NamespaceName = namespaceName;
            Accessibility = accessibility;
            TypeName = typeName;
            HintName = hintName;
            BackingKind = backingKind;
        }

        public string? NamespaceName { get; }

        public string Accessibility { get; }

        public string TypeName { get; }

        public string HintName { get; }

        public StrongIdBackingKind BackingKind { get; }

        public bool Equals(StrongIdModel? other)
        {
            return other is not null &&
                   string.Equals(NamespaceName, other.NamespaceName, StringComparison.Ordinal) &&
                   string.Equals(Accessibility, other.Accessibility, StringComparison.Ordinal) &&
                   string.Equals(TypeName, other.TypeName, StringComparison.Ordinal) &&
                   string.Equals(HintName, other.HintName, StringComparison.Ordinal) &&
                   BackingKind == other.BackingKind;
        }

        public override bool Equals(object? obj)
        {
            return obj is StrongIdModel other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = 17;
                hashCode = (hashCode * 31) + (NamespaceName is null ? 0 : StringComparer.Ordinal.GetHashCode(NamespaceName));
                hashCode = (hashCode * 31) + StringComparer.Ordinal.GetHashCode(Accessibility);
                hashCode = (hashCode * 31) + StringComparer.Ordinal.GetHashCode(TypeName);
                hashCode = (hashCode * 31) + StringComparer.Ordinal.GetHashCode(HintName);
                hashCode = (hashCode * 31) + BackingKind.GetHashCode();
                return hashCode;
            }
        }
    }

    private enum StrongIdBackingKind
    {
        Unsupported,
        Guid,
        Int32,
        Int64
    }
}
