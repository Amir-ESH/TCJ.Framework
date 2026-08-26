# Strongly Typed IDs and Value Objects architecture contract

## Purpose

This document defines the v1 architecture contract for generated strongly typed domain scalars. The first implemented vertical slice is Guid-backed Strongly Typed IDs; additional backing types and integrations are introduced in later tasks without weakening the contract below.

## Strongly Typed IDs

Strongly Typed IDs are generated as immutable `readonly partial record struct` declarations from attributes.

The v1 policy reserves these backing types:

- `Guid`
- `int`
- `long`

The current generator implementation emits Strong ID behavior for `Guid`. Integer-backed generation is intentionally deferred to the later integer-support task.

Generated IDs use record-struct value equality. Default struct values remain possible, and generated APIs expose deterministic `IsDefault` semantics. Primitive conversions are not implicit.

### Minimal Guid-backed example

Declare the ID with one attributed partial record struct:

```csharp
using TCJ.Core.StrongTypes;

[StronglyTypedId<Guid>]
public readonly partial record struct OrderId;
```

The generator supplies an explicit `OrderId(Guid value)` constructor, an immutable `Guid Value` property, `IsDefault`, and a deterministic `ToString()` that uses the canonical GUID `D` format:

```csharp
var value = Guid.Parse("7a29be31-268d-4f2b-babc-fce0ce1cb46c");
var orderId = new OrderId(value);

Guid underlying = orderId.Value;
bool isDefault = orderId.IsDefault;
string text = orderId.ToString();
```

`default(OrderId)` remains valid and reports `IsDefault == true`. No implicit conversion between `OrderId` and `Guid` is generated.

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
