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

### Explicit GUID creation

Guid-backed Strong IDs integrate with `TCJ.Core.Identifiers.IGuidGenerator` through two explicit creation helpers. The generator dependency is supplied by the caller, so creation remains deterministic in tests and generated Strong ID code does not call `Guid.NewGuid()`, resolve services, or obtain time from ambient state:

```csharp
using TCJ.Core.Identifiers;

public sealed class OrderService(IGuidGenerator guidGenerator)
{
    public OrderId CreateRandomId() => OrderId.New(guidGenerator);

    public OrderId CreateTimeOrderedId() => OrderId.NewVersion7(guidGenerator);
}
```

`OrderId.New(generator)` calls `IGuidGenerator.Create()` and wraps the exact returned version 4 GUID. `OrderId.NewVersion7(generator)` calls `IGuidGenerator.CreateVersion7()` and wraps the exact returned version 7 GUID. Both helpers throw `ArgumentNullException` when `generator` is `null`. No equivalent creation helpers are generated for `int`- or `long`-backed IDs.

GUID v7 is not an implicit or universal default. Prefer `NewVersion7` only when the application persistence strategy benefits from roughly time-ordered identifiers, such as reducing index locality costs for GUID primary keys. Use `New` when a random v4 identifier is the intended policy. Keeping the choice at the call site makes the generation policy visible and testable.

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

## Explicit EF Core conversion registration

EF Core integration remains explicit and provider-neutral. Generated Strong IDs expose a nested `StrongIdConversion` class containing two BCL expression trees, `ToBackingValue` and `FromBackingValue`. These expressions do not reference EF Core, so domain projects can declare and use Strong IDs without depending on `TCJ.EntityFrameworkCore`.

Register each Strong ID/backing-type pair explicitly in the EF project, then apply the registry after configuring the entity model:

```csharp
using Microsoft.EntityFrameworkCore;
using TCJ.Core.StrongTypes;
using TCJ.EntityFrameworkCore.Extensions;
using TCJ.EntityFrameworkCore.StrongTypes;

[StronglyTypedId<Guid>]
public readonly partial record struct OrderId;

[StronglyTypedId<int>]
public readonly partial record struct LegacyOrderId;

[StronglyTypedId<long>]
public readonly partial record struct OrderSequence;

public sealed class Order
{
    public OrderId Id { get; set; }

    public LegacyOrderId LegacyNumber { get; set; }

    public OrderSequence Sequence { get; set; }

    public OrderId? ParentOrderId { get; set; }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options)
    : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.HasKey(static order => order.Id);
            entity.Property(static order => order.LegacyNumber);
            entity.Property(static order => order.Sequence);
            entity.Property(static order => order.ParentOrderId);
        });

        var strongIds = new StrongIdConversionRegistry()
            .Register<OrderId, Guid>(
                OrderId.StrongIdConversion.ToBackingValue,
                OrderId.StrongIdConversion.FromBackingValue)
            .Register<LegacyOrderId, int>(
                LegacyOrderId.StrongIdConversion.ToBackingValue,
                LegacyOrderId.StrongIdConversion.FromBackingValue)
            .Register<OrderSequence, long>(
                OrderSequence.StrongIdConversion.ToBackingValue,
                OrderSequence.StrongIdConversion.FromBackingValue);

        modelBuilder.ApplyStrongIdConversions(strongIds);
    }
}
```

`ApplyStrongIdConversions` walks only the EF model metadata already being built; it does not scan application assemblies or discover Strong IDs through reflection. The same registered converter is applied to matching keys, foreign keys, ordinary properties, and nullable wrappers. EF Core keeps database `NULL` values outside value converters, so the non-nullable Strong ID converter is also valid for nullable Strong ID properties. Generated Strong IDs are immutable record structs with value equality, so EF Core's normal struct comparison/snapshot semantics remain correct and no custom mutable-value comparer is required.

Repeated registration with the same generated expression instances is idempotent. Registering the same Strong ID with a different backing type or different expressions, or applying the registry over a property that already has a different converter, fails with an explicit `InvalidOperationException`. The supported primitive backing types are `Guid`, `int`, and `long`. Provider-specific SQL behavior remains outside this registry.

### Minimal API friendliness

The generated `TryParse`/parsing contracts make supported Strong IDs friendly to ASP.NET Core Minimal API route, query, and header binding without adding ASP.NET-specific binder types to the domain model. Model binders and OpenAPI schema integration remain separate integrations.

## Primitive-backed Value Objects

Value Objects are immutable generated record structs backed by a single primitive value with explicit application-owned validation. The v1 generator supports `string`, `Guid`, `int`, `long`, and `decimal` backing types.

A Value Object declaration must be a top-level public or internal `readonly partial record struct` and must provide exactly one static `Result Validate(TValue value)` method. It may optionally provide exactly one static `TValue Normalize(TValue value)` method. TCJ generates an immutable `Value` property, `IsDefault`, a private backing-value constructor, and `Create(TValue)` returning `Result<TValueObject>`. TCJ never invents domain validation or normalization rules.

Construction is deterministic and ordered as `input -> Normalize (optional) -> Validate -> construct`. When `Normalize` is absent, `Create` passes the original input directly to `Validate` and stores that original value after successful validation. When `Normalize` is present, `Validate` always receives the normalized value and successful construction stores that same normalized value. Validation failures continue to preserve every original `ResultError` instance in order. Successful values use normal record-struct equality. The unavoidable `default(TValueObject)` state remains possible for structs and is explicitly observable with `IsDefault`.

Normalization is application-owned, synchronous, and pure by contract. A normalizer should be a deterministic function of its input: it must not perform I/O, resolve services, read databases, consult clocks or environment variables, or depend on mutable global state. TCJ does not inject ambient culture or perform trimming, casing, canonicalization, or any other hidden transformation. When normalization needs culture-independent casing or formatting, choose that behavior explicitly in application code, for example `ToLowerInvariant()`.

### Complete EmailAddress example

```csharp
using System.Collections.Generic;
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;

[ValueObject<string>]
public readonly partial record struct EmailAddress
{
    private static string Normalize(string value)
        => value.Trim().ToLowerInvariant();

    private static Result Validate(string value)
    {
        var errors = new List<ResultError>();

        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ResultError("email.required", "Email is required."));
        }

        if (!string.IsNullOrWhiteSpace(value) && !value.Contains('@'))
        {
            errors.Add(new ResultError("email.format", "Email must contain an '@' character."));
        }

        return errors.Count == 0
            ? Result.Success()
            : Result.Failure(errors);
    }
}

Result<EmailAddress> emailResult = EmailAddress.Create("  Customer@Example.com  ");

if (emailResult.IsSuccess)
{
    EmailAddress email = emailResult.Value;
    string underlying = email.Value; // "customer@example.com"
    bool isDefault = email.IsDefault; // false
}

Result<EmailAddress> invalid = EmailAddress.Create("");
IReadOnlyList<ResultError> errors = invalid.Errors;
```

The generated `EmailAddress(string)` constructor is private. Normal non-default creation therefore goes through `EmailAddress.Create(...)`; `default(EmailAddress)` remains possible by normal CLR struct semantics and reports `IsDefault == true`.

### Complete numeric Value Object example

```csharp
using TCJ.Core.Results;
using TCJ.Core.StrongTypes;

[ValueObject<decimal>]
public readonly partial record struct MoneyAmount
{
    private static Result Validate(decimal value)
        => value < 0m
            ? Result.Failure(new ResultError(
                "money.negative",
                "Money amount cannot be negative."))
            : Result.Success();
}

Result<MoneyAmount> amountResult = MoneyAmount.Create(125.50m);
MoneyAmount amount = amountResult.Value;
decimal underlying = amount.Value;

Result<MoneyAmount> rejected = MoneyAmount.Create(-1m);
bool failed = rejected.IsFailure; // true
```

The same generation contract applies to `Guid`, `int`, and `long` backing types. Primitive conversions are not generated implicitly.

### Parsing and System.Text.Json boundary contract

Generated Value Objects implement `IParsable<TSelf>` and `ISpanParsable<TSelf>`. Text input is converted to the backing primitive with a deterministic wire-oriented parser and then passed to `Create(TValue)`, so parsing cannot bypass application normalization or validation:

| Backing type | Text parsing contract | JSON scalar |
| --- | --- | --- |
| `string` | The supplied text is the candidate value; `Normalize` and `Validate` still run | JSON string |
| `Guid` | Canonical `D` format | JSON string |
| `int` | `NumberStyles.Integer` + `InvariantCulture` | JSON number |
| `long` | `NumberStyles.Integer` + `InvariantCulture` | JSON number |
| `decimal` | `AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign | AllowDecimalPoint` + `InvariantCulture` | JSON number |

Provider arguments are intentionally ignored. `TryParse` returns `false` and the default Value Object when either primitive parsing or domain validation fails. `Parse` throws a generic `FormatException` for rejected text and does not embed the rejected input or application `ResultError` details in its message.

Every supported Value Object also receives a dedicated generated `ValueObjectJsonConverter : JsonConverter<TValueObject>` and is annotated to use it. Serialization writes only the underlying scalar; it never exposes `{ "value": ... }`, `Result`, or validation-error internals. Deserialization reads only the expected scalar token, then calls `Create(TValue)`. A failed `Result` becomes a generic `JsonException`, so normalization and validation remain mandatory while the raw rejected value and validation messages stay out of converter-generated exceptions. JSON `null` is rejected for the non-nullable Value Object contract. A default string-backed Value Object is also rejected during serialization because its unavoidable struct-default backing value is `null` and therefore cannot represent a valid non-null string Value Object.

The generated converter is closed over the concrete Value Object type and does not use `JsonConverterFactory`, runtime type scanning, `MakeGenericType`, `Activator`, or reflection. Native AOT consumers should include generated Value Object types in a source-generated `JsonSerializerContext`. When TCJ and System.Text.Json generators run in the same compilation, explicitly register each concrete `ValueObjectJsonConverter` before constructing the context, exactly as with generated Strong ID converters.

Composite multi-field value objects are not generated; consumers should use normal records for those cases. EF Core integration remains a separate concern.

## Examples

Implemented strong-type examples include:

- `OrderId` as a Guid-backed strongly typed ID.
- `EmailAddress` as a validated string-backed Value Object.
- `MoneyAmount` as a validated decimal-backed Value Object.

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
