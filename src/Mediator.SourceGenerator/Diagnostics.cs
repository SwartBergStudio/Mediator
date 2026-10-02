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
        messageFormat: "Open generic handler '{0}' is not registered by AddMediatorHandlers(); register it explicitly with services.AddTransient(typeof(...), typeof(...))",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);
}
