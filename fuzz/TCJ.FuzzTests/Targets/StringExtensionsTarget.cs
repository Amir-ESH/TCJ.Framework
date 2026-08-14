using System.Text;
using TCJ.Core.Extensions;

namespace TCJ.FuzzTests.Targets;

internal sealed class StringExtensionsTarget : IFuzzTarget
{
    public string Name => "StringExtensions";

    public void Execute(ReadOnlyMemory<byte> input)
    {
        string value = Encoding.UTF8.GetString(input.Span);
        string normalized = value.NormalizeLineEndings();
        if (normalized.NormalizeLineEndings() != normalized)
            throw new FuzzInvariantException("Line-ending normalization is not idempotent.");

        string prefixed = value.EnsureStartsWith('x');
        string suffixed = value.EnsureEndsWith('x');
        if (!prefixed.StartsWith('x') || !suffixed.EndsWith('x'))
            throw new FuzzInvariantException("Prefix or suffix invariant failed.");

        int max = input.Length == 0 ? 0 : input.Span[0] % 64;
        if (value.Truncate(max)?.Length > max)
            throw new FuzzInvariantException("Truncation exceeded the requested size.");

        _ = value.ToCamelCase(normalizeAcronyms: true);
        _ = value.ToPascalCase();
        _ = value.ToKebabCase();
        _ = value.ToSnakeCase();
        _ = value.NormalizeWhitespace();
        _ = value.SplitLines();
    }
}
