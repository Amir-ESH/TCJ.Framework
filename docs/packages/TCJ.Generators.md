# TCJ.Generators

TCJ.Generators contains incremental Roslyn source generators used by TCJ strong-type features.

The package generates Strongly Typed IDs as `readonly partial record struct` declarations for these backing types:

| Backing type | Default/wire representation | Default value |
| --- | --- | --- |
| `Guid` | Canonical invariant `D` format | `Guid.Empty` |
| `int` | Invariant base-10 integer text | `0` |
| `long` | Invariant base-10 integer text | `0` |

Generated members include an explicit value constructor, immutable `Value`, deterministic `IsDefault`, string and span `Parse`/`TryParse`, `IParsable<TSelf>`/`ISpanParsable<TSelf>`, `IFormattable`/`ISpanFormattable`, allocation-friendly `TryFormat`, and explicit conversions to and from the backing primitive. Implicit conversions remain disabled.

Numeric parsing uses `NumberStyles.Integer` with `CultureInfo.InvariantCulture`; provider arguments are ignored so current culture cannot change wire behavior. Boundary values, zero, and negative values round-trip exactly; overflow fails according to the underlying BCL integer parsing contract.

JSON integration and EF Core integration remain separate concerns.
