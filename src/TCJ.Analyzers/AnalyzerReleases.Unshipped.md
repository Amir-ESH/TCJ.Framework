; Unshipped analyzer release
; Add every new diagnostic here before it is merged or packed.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCJ0001 | TCJ.DependencyInjection | Error | Reports public concrete dependency types that implement multiple TCJ lifetime markers.
TCJ0002 | TCJ.DependencyInjection | Error | Reports interface-registration lifetime markers on public concrete dependency types that expose no eligible service interface.
