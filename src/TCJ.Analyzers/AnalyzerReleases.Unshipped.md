; Unshipped analyzer release
; Add every new diagnostic here before it is merged or packed.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCJ0001 | TCJ.DependencyInjection | Error | Reports effectively public concrete dependency types that implement multiple TCJ lifetime markers.
TCJ0002 | TCJ.DependencyInjection | Error | Reports interface-registration lifetime markers on effectively public concrete dependency types that expose no eligible service interface.
TCJ0003 | TCJ.DependencyInjection | Error | Reports convention-marked concrete dependency types that are not effectively public.
TCJ0004 | TCJ.DependencyInjection | Warning | Reports effectively public concrete domain-event handlers that implement TCJ lifetime markers ignored by the handler registration pipeline.
TCJ1000 | TCJ.Persistence | Warning | Reports persistence commits performed from concrete TCJ repository implementations.
