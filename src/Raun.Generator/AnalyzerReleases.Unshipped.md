; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
RAUN000 | Raun.Usage | Error | Unhandled exception in Raun generator
RAUN001 | Raun.Usage | Error | Scenario method must be async Task or async ValueTask
RAUN002 | Raun.Usage | Error | Unsupported scenario statement
RAUN003 | Raun.Usage | Error | Unsupported control flow in scenario (loops, switch, try, goto)
RAUN004 | Raun.Usage | Error | Scenario step must be a phase-marker call
RAUN005 | Raun.Usage | Error | DSL method has an unsupported return type
RAUN006 | Raun.Usage | Error | Parallel group element must be a phase-marker call
RAUN007 | Raun.Usage | Error | Scenario step argument is not lowerable
RAUN008 | Raun.Usage | Warning | Display-name placeholder does not bind to a parameter
RAUN009 | Raun.Usage | Error | Resource access must be declared
RAUN010 | Raun.Usage | Error | Lineage subject must name a step subject
RAUN011 | Raun.Usage | Error | Scenario condition must be an awaited phase-marker call
RAUN012 | Raun.Usage | Error | Conditionally assigned local has no step-produced definition
RAUN013 | Raun.Usage | Error | Parallel steps conflict on one resource
RAUN014 | Raun.Usage | Error | Cleanup uses the registering step's context
RAUN015 | Raun.Usage | Error | Contended resource must declare exactly one kind
RAUN016 | Raun.Usage | Warning | Contended resource use never constrains anything
