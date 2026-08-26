# TCJ.Generators

TCJ.Generators contains incremental Roslyn source generators used by TCJ strong-type features.

The package currently generates Guid-backed Strongly Typed IDs as `readonly partial record struct` declarations. Generated members include an explicit value constructor, immutable `Value`, deterministic `IsDefault`, canonical invariant `D`-format output, `Parse`/`TryParse` string and span APIs, `IParsable<TSelf>`/`ISpanParsable<TSelf>`, `IFormattable`/`ISpanFormattable`, allocation-friendly `TryFormat`, and explicit conversions to and from `Guid`. Implicit conversions remain disabled. JSON integration, EF Core integration, and integer-backed IDs are intentionally deferred to later tasks.
