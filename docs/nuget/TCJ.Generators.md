# TCJ.Generators

TCJ.Generators contains incremental Roslyn source generators used by TCJ strong-type features.

The package generates Strongly Typed IDs as `readonly partial record struct` declarations for these backing types:

| Backing type | Default text/wire representation | JSON representation | Default value |
| --- | --- | --- | --- |
| `Guid` | Canonical invariant `D` format | JSON string in canonical `D` format | `Guid.Empty` |
| `int` | Invariant base-10 integer text | JSON number | `0` |
| `long` | Invariant base-10 integer text | JSON number | `0` |

Generated members include an explicit value constructor, immutable `Value`, deterministic `IsDefault`, string and span `Parse`/`TryParse`, `IParsable<TSelf>`/`ISpanParsable<TSelf>`, `IFormattable`/`ISpanFormattable`, allocation-friendly `TryFormat`, explicit conversions to and from the backing primitive, and a dedicated `System.Text.Json` converter. Implicit conversions remain disabled.

Numeric parsing uses `NumberStyles.Integer` with `CultureInfo.InvariantCulture`; provider arguments are ignored so current culture cannot change wire behavior. Boundary values, zero, and negative values round-trip exactly; overflow fails according to the underlying BCL integer parsing contract.

## System.Text.Json contract

Strong IDs serialize as their backing scalar rather than as an object containing `Value`:

```json
"7a29be31-268d-4f2b-babc-fce0ce1cb46c"
```

```json
-42
```

Each generated ID has its own public nested `StrongIdJsonConverter : JsonConverter<TStrongId>` and is annotated to use that converter. The generator does not emit `JsonConverterFactory`, `MakeGenericType`, runtime type scanning, or reflection-based converter discovery code.

Deserialization accepts only the backing scalar token kind: a JSON string for `Guid` IDs and a JSON number for `int`/`long` IDs. Wrong token kinds, invalid GUID text, non-integral or overflowing numeric values, and `null` for a non-nullable Strong ID throw `JsonException`. Generated converter error messages describe the invalid contract without embedding the raw malformed scalar value.

Native AOT consumers should use System.Text.Json source-generated metadata and include their Strong ID types in a `JsonSerializerContext`. Metadata generation is required for converter-backed IDs; do not rely on reflection-based serialization defaults. When the context and Strong IDs are generated in the same compilation, register each generated converter explicitly before constructing the context, for example:

```csharp
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
options.Converters.Add(new OrderId.StrongIdJsonConverter());
var context = new AppJsonContext(options);
```

This explicit registration is closed over the concrete Strong ID type and does not use runtime type scanning, reflection, `MakeGenericType`, or a converter factory.

EF Core integration remains a separate concern.
