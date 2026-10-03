; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MEDGEN001 | SwartBerg.Mediator | Warning | SwartBerg.Mediator is not referenced
MEDGEN002 | SwartBerg.Mediator | Warning | Handler is not accessible to generated code
MEDGEN003 | SwartBerg.Mediator | Info | Open generic handler requires manual registration
MEDGEN004 | SwartBerg.Mediator | Warning | Invalid pipeline behavior declaration
MEDGEN005 | SwartBerg.Mediator | Warning | Request has no handler
MEDGEN006 | SwartBerg.Mediator | Warning | Request has more than one handler
