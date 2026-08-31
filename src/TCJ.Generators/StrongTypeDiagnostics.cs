using Microsoft.CodeAnalysis;

namespace TCJ.Generators;

internal static class StrongTypeDiagnostics
{
    internal const string NonPartialDiagnosticId = "TCJ4000";
    internal const string UnsupportedShapeDiagnosticId = "TCJ4001";
    internal const string UnsupportedBackingTypeDiagnosticId = "TCJ4002";
    internal const string GenericDeclarationDiagnosticId = "TCJ4003";
    internal const string GeneratedMemberCollisionDiagnosticId = "TCJ4004";
    internal const string AmbiguousAttributesDiagnosticId = "TCJ4005";
    internal const string InvalidValueObjectDeclarationDiagnosticId = "TCJ4006";
    internal const string ValueObjectGeneratedMemberCollisionDiagnosticId = "TCJ4007";

    private const string Category = "TCJ.StrongTypes";

    internal static readonly DiagnosticDescriptor NonPartial = new(
        NonPartialDiagnosticId,
        "Strong ID declaration must be partial",
        "Strong ID '{0}' must be declared partial",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ Strong ID generation requires the attributed declaration to be partial so the generator can add the supported API surface.");

    internal static readonly DiagnosticDescriptor UnsupportedShape = new(
        UnsupportedShapeDiagnosticId,
        "Strong ID declaration has an unsupported shape",
        "Strong ID '{0}' must be a top-level public or internal readonly record struct",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ Strong IDs must use the supported top-level public or internal readonly record struct declaration shape.");

    internal static readonly DiagnosticDescriptor UnsupportedBackingType = new(
        UnsupportedBackingTypeDiagnosticId,
        "Strong ID backing type is unsupported",
        "Strong ID '{0}' uses unsupported backing type '{1}'; supported backing types are Guid, int, and long",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ Strong ID generation supports System.Guid, System.Int32, and System.Int64 backing values.");

    internal static readonly DiagnosticDescriptor GenericDeclaration = new(
        GenericDeclarationDiagnosticId,
        "Generic Strong ID declarations are unsupported",
        "Strong ID '{0}' must not declare type parameters",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ Strong ID declarations must be non-generic because the generated API is closed over one concrete identifier type.");

    internal static readonly DiagnosticDescriptor GeneratedMemberCollision = new(
        GeneratedMemberCollisionDiagnosticId,
        "Strong ID member conflicts with generated API",
        "Member '{0}' on Strong ID '{1}' conflicts with the generated Strong ID API",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ reports user-defined members whose names or signatures collide with members that the Strong ID generator must emit.");

    internal static readonly DiagnosticDescriptor AmbiguousAttributes = new(
        AmbiguousAttributesDiagnosticId,
        "Strong type attributes are ambiguous",
        "Strong type attribute '{0}' on '{1}' is ambiguous; use exactly one StronglyTypedId<T> attribute and do not combine it with ValueObject<T>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A declaration must select one unambiguous TCJ strong-type generation contract.");

    internal static readonly DiagnosticDescriptor InvalidValueObjectDeclaration = new(
        InvalidValueObjectDeclarationDiagnosticId,
        "Value Object declaration is invalid",
        "Value Object '{0}' cannot be generated because {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ Value Objects must use the supported readonly partial record struct shape, a supported primitive backing type, exactly one application-defined static Result Validate(TValue value) method, and when normalization is declared, exactly one static TValue Normalize(TValue value) method.");

    internal static readonly DiagnosticDescriptor ValueObjectGeneratedMemberCollision = new(
        ValueObjectGeneratedMemberCollisionDiagnosticId,
        "Value Object member conflicts with generated API",
        "Member '{0}' on Value Object '{1}' conflicts with the generated Value Object API",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "TCJ reserves generated Value Object member names and constructors so Create remains the normal validated construction path.");
}
