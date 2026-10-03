using Microsoft.CodeAnalysis;

namespace Mediator.SourceGenerator;

internal static class Diagnostics
{
    private const string Category = "SwartBerg.Mediator";

    public static readonly DiagnosticDescriptor MediatorNotReferenced = new(
        id: "MEDGEN001",
        title: "SwartBerg.Mediator is not referenced",
        messageFormat: "SwartBerg.Mediator.SourceGenerator requires a reference to the SwartBerg.Mediator package{0}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InaccessibleHandler = new(
        id: "MEDGEN002",
        title: "Handler is not accessible to generated code",
        messageFormat: "Handler '{0}' or one of its message types is private or protected and cannot be registered by AddMediatorHandlers(); make it internal or public",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor OpenGenericHandler = new(
        id: "MEDGEN003",
        title: "Open generic handler requires manual registration",
        messageFormat: "Open generic handler '{0}' is not registered by AddMediatorHandlers(); list it in [assembly: MediatorPipelineBehaviors(...)] or register it explicitly",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingHandler = new(
        id: "MEDGEN005",
        title: "Request has no handler",
        messageFormat: "Request '{0}' has no handler in this project, so sending it fails at runtime; add a handler or suppress MEDGEN005 if it is handled in another project",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor DuplicateHandlers = new(
        id: "MEDGEN006",
        title: "Request has more than one handler",
        messageFormat: "Request '{0}' has {1} handlers ({2}); only the last one registered is used, so the others never run",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor InvalidDeclaredBehavior = new(
        id: "MEDGEN004",
        title: "Invalid pipeline behavior declaration",
        messageFormat: "'{0}' in [assembly: MediatorPipelineBehaviors] must be a non-abstract open generic type implementing IPipelineBehavior<,>, IPipelineBehavior<> or IStreamPipelineBehavior<,> with its own type parameters",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
}
