using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TCJ.EntityFrameworkCore.StrongTypes;

internal sealed class StrongIdConversionRegistration(
    Type backingType,
    LambdaExpression toBackingValue,
    LambdaExpression fromBackingValue,
    ValueConverter converter)
{
    internal Type BackingType { get; } = backingType;

    internal LambdaExpression ToBackingValue { get; } = toBackingValue;

    internal LambdaExpression FromBackingValue { get; } = fromBackingValue;

    internal ValueConverter Converter { get; } = converter;
}
