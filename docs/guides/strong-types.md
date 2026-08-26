# Strongly Typed IDs and Value Objects architecture contract

## Purpose

This document defines the v1 architecture contract for generated strongly typed domain scalars. The first implemented vertical slice is Guid-backed Strongly Typed IDs; additional backing types and integrations are introduced in later tasks without weakening the contract below.

## Strongly Typed IDs

Strongly Typed IDs are generated as immutable `readonly partial record struct` declarations from attributes.

The v1 generator supports these backing types:

| Backing type | Default/wire representation | Parsing semantics | Default value |
| --- | --- | --- | --- |
| `Guid` | Canonical `D` format | Canonical `D`, culture-stable | `Guid.Empty` |
| `int` | Invariant base-10 integer text | `NumberStyles.Integer` + `InvariantCulture` | `0` |
| `long` | Invariant base-10 integer text | `NumberStyles.Integer` + `InvariantCulture` | `0` |

Other numeric backing types are not supported in v1 and do not receive generated Strong ID members.

Generated IDs use record-struct value equality. Default struct values remain possible, and generated APIs expose deterministic `IsDefault` semantics. Primitive conversions are not implicit.

### Minimal Guid-backed example

Declare the ID with one attributed partial record struct:

```csharp
using TCJ.Core.StrongTypes;

[StronglyTypedId<Guid>]
public readonly partial record struct OrderId;
```

The generator supplies an explicit `OrderId(Guid value)` constructor, an immutable `Guid Value` property, `IsDefault`, culture-stable parsing/formatting APIs, and explicit conversion operators. The default wire representation is the canonical GUID `D` format:

```csharp
var value = Guid.Parse("7a29be31-268d-4f2b-babc-fce0ce1cb46c");
var orderId = new OrderId(value);

Guid underlying = orderId.Value;
bool isDefault = orderId.IsDefault;
string text = orderId.ToString();

OrderId parsed = OrderId.Parse(text);
bool parsedSuccessfully = OrderId.TryParse(text, out OrderId parsedAgain);

OrderId fromGuid = (OrderId)value;
Guid backToGuid = (Guid)fromGuid;
```

`default(OrderId)` remains valid and reports `IsDefault == true`. No implicit conversion between `OrderId` and `Guid` is generated; conversions in both directions require an explicit cast and preserve the exact underlying value.

### Parsing and formatting contract

Guid-backed Strong IDs implement `IParsable<TSelf>`, `ISpanParsable<TSelf>`, `IFormattable`, and `ISpanFormattable`. The generated string and span parsing APIs accept the canonical GUID `D` wire form. `TryParse` returns `false` and the default ID for ordinary invalid input instead of throwing, while `Parse` follows the normal parsing contract and throws `FormatException` for malformed text.

The default `ToString()` and `TryFormat` output is always canonical `D` format and uses invariant/culture-stable GUID semantics. Provider arguments are intentionally ignored so changing `CurrentCulture` cannot change the wire representation. Explicit non-default GUID format specifiers can still be requested through the formatting interfaces; those formatted alternatives are not the default wire form.

The span-based parsing and formatting overloads avoid intermediate strings at application boundaries where callers already have spans.

### Integer-backed IDs

`int`- and `long`-backed Strong IDs expose the same parsing/formatting interface surface as Guid-backed IDs:

```csharp
using TCJ.Core.StrongTypes;

[StronglyTypedId<int>]
public readonly partial record struct OrderId;

[StronglyTypedId<long>]
public readonly partial record struct EventId;
```

Their default wire representation is the invariant base-10 representation of the underlying integer. Parsing uses `NumberStyles.Integer` with `CultureInfo.InvariantCulture`, so signs and the normal integer whitespace accepted by that style are supported, while culture-specific thousands separators are not part of the wire contract.

Provider arguments on generated parsing and formatting APIs are intentionally ignored. This keeps wire behavior stable when `CurrentCulture` changes. Explicit numeric format strings are still available through `IFormattable`/`ISpanFormattable` for display scenarios and are evaluated with invariant culture; alternate display formats are not the default wire representation.

Boundary behavior follows the underlying BCL integer contract:

- negative values and `int.MinValue`/`int.MaxValue` or `long.MinValue`/`long.MaxValue` round-trip exactly;
- `0` is accepted, is the default value, and reports `IsDefault == true`;
- `TryParse` returns `false` and the default ID for malformed or overflowing input;
- `Parse` throws `FormatException` for malformed input and `OverflowException` for values outside the backing type range;
- conversions in both directions are explicit and preserve the exact underlying numeric value.

For example:

```csharp
OrderId id = (OrderId)(-42);
string wireText = id.ToString(); // "-42"
OrderId parsed = OrderId.Parse(wireText);
int value = (int)parsed;
```

### System.Text.Json scalar contract

Every supported Strong ID receives a dedicated generated `StrongIdJsonConverter : JsonConverter<TStrongId>` and is annotated to use that converter. JSON remains a scalar compatibility contract rather than exposing the generated `Value` member as an object property:

- `Guid`-backed IDs serialize as canonical `D`-format JSON strings, for example `"7a29be31-268d-4f2b-babc-fce0ce1cb46c"`;
- `int`- and `long`-backed IDs serialize as JSON numbers, for example `-42`;
- Strong IDs are never serialized as `{ "value": ... }` objects.

Deserialization reconstructs the Strong ID through its generated value constructor. A `Guid` ID accepts only a JSON string containing a valid GUID value, while numeric IDs accept only JSON numbers representable by their exact backing type. Wrong token kinds, malformed scalar values, overflow, fractional values for integer IDs, and JSON `null` for a non-nullable Strong ID throw `JsonException`. Converter-generated exception messages intentionally avoid echoing the malformed scalar value.

The converter path is closed and static per ID type. Generated code does not use `JsonConverterFactory`, `MakeGenericType`, runtime type scanning, `Activator`, or reflection-based converter discovery code. This preserves the Strong ID AOT-first contract.

Native AOT consumers must still follow the normal System.Text.Json AOT rule: include Strong ID types in a source-generated `JsonSerializerContext` and use metadata-based serialization when custom converters participate. If the context and Strong IDs are generated in the same compilation, explicitly add each generated `StrongIdJsonConverter` to the `JsonSerializerOptions.Converters` collection before constructing the context; Roslyn generators do not consume another generator's newly emitted source in the same generation pass. This registration is static and does not require reflection or runtime type scanning.

### Minimal API friendliness

The generated `TryParse`/parsing contracts make supported Strong IDs friendly to ASP.NET Core Minimal API route, query, and header binding without adding ASP.NET-specific binder types to the domain model. Model binders and OpenAPI schema integration remain separate integrations.

## Primitive-backed Value Objects

Value Objects are immutable generated record structs backed by a single primitive value with explicit validation.

Supported v1 backing types:

- `string`
- `Guid`
- `int`
- `long`
- `decimal`

Composite multi-field value objects are not generated; consumers should use normal records for those cases.

## Examples

Planned and implemented strong-type examples include:

- `OrderId` as a Guid-backed strongly typed ID
- `EmailAddress` as a primitive-backed Value Object

## Package ownership

- `TCJ.Core`: domain-safe contracts and runtime abstractions.
- `TCJ.Generators`: compile-time source generators.
- `TCJ.EntityFrameworkCore`: persistence integration only.
- `TCJ.AspNetCore`: HTTP/API integration only.

Domain projects do not depend on EF Core to declare IDs or Value Objects.

## Compatibility rules

- No implicit primitive conversions.
- Parsing must be culture-stable.
- Generated public member names must be reserved and collision checked.
- Generated code must not require runtime reflection and must remain compatible with Native AOT expectations.

## Non-goals

- Arbitrary composite generated Value Objects.
- Reflection-based equality.
- Hidden domain behavior in base classes.
- Generator implementation before this contract is reviewed.
