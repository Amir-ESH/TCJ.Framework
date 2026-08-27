; Unshipped generator diagnostic release
; Add every new generator diagnostic here before it is merged or packed.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCJ4000 | TCJ.StrongTypes | Error | Reports Strong ID declarations that are not partial.
TCJ4001 | TCJ.StrongTypes | Error | Reports Strong ID declaration shapes that are not supported top-level public or internal readonly record structs.
TCJ4002 | TCJ.StrongTypes | Error | Reports Strong ID backing types other than Guid, int, or long.
TCJ4003 | TCJ.StrongTypes | Error | Reports generic Strong ID declarations.
TCJ4004 | TCJ.StrongTypes | Error | Reports user-defined members that collide with generated Strong ID API members.
TCJ4005 | TCJ.StrongTypes | Error | Reports duplicate or ambiguous TCJ strong-type attributes on one declaration.
TCJ4006 | TCJ.StrongTypes | Error | Reports invalid primitive Value Object declarations, backing types, or Validate signatures.
TCJ4007 | TCJ.StrongTypes | Error | Reports user-defined constructors or members that collide with the generated Value Object API.
