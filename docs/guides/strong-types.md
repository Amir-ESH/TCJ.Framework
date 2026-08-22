# Strongly Typed IDs and Value Objects architecture contract

## Purpose

This document defines the v1 architecture contract for future generated strongly typed domain scalars. It intentionally defines rules before generator APIs are introduced.

## Strongly Typed IDs

Strongly Typed IDs are generated as immutable `readonly partial record struct` declarations from attributes.

Supported v1 backing types:

- `Guid`
- `int`
- `long`

Generated IDs use record-struct value equality. Default struct values remain possible, and generated APIs expose a deterministic `IsDefault` concept.

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

Future generated examples include:

- `OrderId` as a strongly typed ID
- `EmailAddress` as a primitive-backed Value Object

## Package ownership

- `TCJ.Core`: domain-safe contracts and runtime abstractions.
- `TCJ.Generators`: future source generators.
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
