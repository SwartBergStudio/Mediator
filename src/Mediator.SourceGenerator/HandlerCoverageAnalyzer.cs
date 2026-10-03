using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Mediator.SourceGenerator;

/// <summary>
/// Build-time checks that would otherwise only fail at runtime:
/// MEDGEN005 (a request declared in this project has no handler) and MEDGEN006 (a request has several handlers; the
/// DI container only uses the last one registered).
/// </summary>
/// <remarks>
/// An analyzer rather than part of the generator, so the warnings can be suppressed per type with
/// <c>#pragma warning disable</c> or <c>[SuppressMessage]</c>, or per project with <c>.editorconfig</c> / <c>NoWarn</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HandlerCoverageAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Diagnostics.MissingHandler, Diagnostics.DuplicateHandlers);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            if (start.Compilation.GetTypeByMetadataName("Mediator.IMediator") is null)
                return;

            var requests = new ConcurrentBag<(string HandlerService, INamedTypeSymbol Request)>();
            var handlers = new ConcurrentBag<(string HandlerService, string Implementation)>();

            start.RegisterSymbolAction(symbolContext =>
            {
                var type = (INamedTypeSymbol)symbolContext.Symbol;
                if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || MediatorHandlerGenerator.IsOpenGeneric(type))
                    return;

                foreach (var handlerInterface in type.AllInterfaces.Where(MediatorHandlerGenerator.IsHandlerInterface))
                {
                    if (handlerInterface.OriginalDefinition.MetadataName is "IRequestHandler`2" or "IRequestHandler`1" or "IStreamRequestHandler`2")
                        handlers.Add((MediatorHandlerGenerator.FullName(handlerInterface), MediatorHandlerGenerator.FullName(type)));
                }

                foreach (var service in RequiredHandlerServices(type))
                    requests.Add((service, type));
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                var handlersByService = handlers
                    .GroupBy(h => h.HandlerService, System.StringComparer.Ordinal)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(h => h.Implementation).Distinct().OrderBy(n => n, System.StringComparer.Ordinal).ToList(),
                        System.StringComparer.Ordinal);

                foreach (var (service, request) in requests.Distinct())
                {
                    var location = request.Locations.FirstOrDefault();
                    var name = request.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

                    if (!handlersByService.TryGetValue(service, out var implementations))
                    {
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.MissingHandler, location, name));
                    }
                    else if (implementations.Count > 1)
                    {
                        var names = string.Join(", ", implementations.Select(i => i.Replace("global::", string.Empty)));
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.DuplicateHandlers, location, name, implementations.Count, names));
                    }
                }
            });
        });
    }

    /// <summary>
    /// The handler services a concrete request type needs: one per request interface it implements.
    /// </summary>
    private static IEnumerable<string> RequiredHandlerServices(INamedTypeSymbol type)
    {
        var request = MediatorHandlerGenerator.FullName(type);
        foreach (var candidate in type.AllInterfaces)
        {
            var definition = candidate.OriginalDefinition;
            if (definition.ContainingNamespace is not { Name: MediatorHandlerGenerator.MediatorNamespace, ContainingNamespace.IsGlobalNamespace: true } ||
                definition.ContainingAssembly?.Name != MediatorHandlerGenerator.MediatorAssembly)
                continue;

            var service = definition.MetadataName switch
            {
                "IRequest`1" => $"global::Mediator.IRequestHandler<{request}, {MediatorHandlerGenerator.FullName(candidate.TypeArguments[0])}>",
                "IRequest" => $"global::Mediator.IRequestHandler<{request}>",
                "IStreamRequest`1" => $"global::Mediator.IStreamRequestHandler<{request}, {MediatorHandlerGenerator.FullName(candidate.TypeArguments[0])}>",
                _ => null,
            };
            if (service != null)
                yield return service;
        }
    }
}
