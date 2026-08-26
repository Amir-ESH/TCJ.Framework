# TCJ.Generators

TCJ.Generators contains incremental Roslyn source generators used by TCJ strong-type features.

The package currently generates the first Strongly Typed ID vertical slice for `Guid`-backed `readonly partial record struct` declarations. Generated members include an explicit value constructor, immutable `Value`, deterministic `IsDefault`, and canonical GUID `ToString()` behavior. Parsing, explicit conversion operators, JSON integration, EF Core integration, and integer-backed IDs are intentionally deferred to later tasks.
