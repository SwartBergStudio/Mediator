using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Mediator.SourceGenerator;

/// <summary>
/// Generates <c>AddMediatorHandlers()</c>, which registers every handler in the project and pre-creates the
/// strongly-typed dispatchers, so SwartBerg.Mediator needs no reflection at runtime.
/// </summary>
/// <remarks>
/// Discovery mirrors <c>AddMediator(assemblies)</c>: non-abstract, closed classes implementing
/// IRequestHandler&lt;,&gt;, IRequestHandler&lt;&gt;, INotificationHandler&lt;&gt;, IStreamRequestHandler&lt;,&gt;
/// or IStreamPipelineBehavior&lt;,&gt; are registered as transient services.
/// </remarks>
[Generator(LanguageNames.CSharp)]
public sealed class MediatorHandlerGenerator : IIncrementalGenerator
{
    private const string MediatorNamespace = "Mediator";
    private const string MediatorAssembly = "Mediator";
    private const string ClassName = "MediatorHandlerRegistrations";

    private static readonly Dictionary<string, HandlerKind> s_handlerInterfaces = new()
    {
        ["IRequestHandler`2"] = HandlerKind.Request,
        ["IRequestHandler`1"] = HandlerKind.Command,
        ["INotificationHandler`1"] = HandlerKind.Notification,
        ["IStreamRequestHandler`2"] = HandlerKind.StreamRequest,
        ["IStreamPipelineBehavior`2"] = HandlerKind.StreamBehavior,
    };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var handlerClasses = context.SyntaxProvider
            .CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null }
                                 || node is RecordDeclarationSyntax { BaseList: not null } record && !record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword),
                static (ctx, ct) => Analyze(ctx, ct))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!)
            .Collect();

        var settings = context.CompilationProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) => new GeneratorSettings(
                GetNamespace(pair.Left, pair.Right.GlobalOptions),
                pair.Left.GetTypeByMetadataName("Mediator.MediatorRegistry") is not null,
                new EquatableArray<DiagnosticInfo>(BehaviorDeclarations.Read(pair.Left).Diagnostics)));

        context.RegisterSourceOutput(handlerClasses.Combine(settings), static (spc, input) => Execute(spc, input.Left, input.Right));
    }

    private static HandlerClassResult? Analyze(GeneratorSyntaxContext context, CancellationToken cancellationToken)
    {
        if (context.SemanticModel.GetDeclaredSymbol(context.Node, cancellationToken) is not INamedTypeSymbol type)
            return null;

        // Partial classes are visited once per declaration; only analyze the first one.
        if (type.DeclaringSyntaxReferences.Length > 1 && type.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) != context.Node)
            return null;

        if (type.IsAbstract || type.IsStatic || type.TypeKind != TypeKind.Class)
            return null;

        var handlerInterfaces = type.AllInterfaces.Where(IsHandlerInterface).ToList();
        if (handlerInterfaces.Count == 0)
            return null;

        var location = LocationInfo.From(type.Locations.FirstOrDefault());
        var display = type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

        if (IsOpenGeneric(type))
        {
            // Behaviors listed in [assembly: MediatorPipelineBehaviors] are registered closed per request.
            var declared = BehaviorDeclarations.Read(context.SemanticModel.Compilation).Declarations
                .Any(d => SymbolEqualityComparer.Default.Equals(d.Type, type.OriginalDefinition));
            return declared ? null : new HandlerClassResult(default, new DiagnosticInfo(Diagnostics.OpenGenericHandler, location, display));
        }

        if (!IsAccessible(type) || handlerInterfaces.Any(i => !IsAccessible(i)))
            return new HandlerClassResult(default, new DiagnosticInfo(Diagnostics.InaccessibleHandler, location, display));

        var implementation = FullName(type);
        var registrations = ImmutableArray.CreateBuilder<HandlerRegistration>();
        var declaredBehaviors = BehaviorDeclarations.Read(context.SemanticModel.Compilation).Declarations;

        foreach (var handlerInterface in handlerInterfaces)
        {
            var kind = s_handlerInterfaces[handlerInterface.OriginalDefinition.MetadataName];
            registrations.Add(new HandlerRegistration(
                kind,
                FullName(handlerInterface),
                implementation,
                FullName(handlerInterface.TypeArguments[0]),
                handlerInterface.TypeArguments.Length > 1 ? FullName(handlerInterface.TypeArguments[1]) : null));

            // Close behaviors declared with [assembly: MediatorPipelineBehaviors(...)] over this request.
            foreach (var declaration in declaredBehaviors)
            {
                if (declaration.TargetKind != kind) continue;

                var closed = BehaviorDeclarations.TryClose(declaration, handlerInterface.TypeArguments, context.SemanticModel.Compilation);
                if (closed is null || !IsAccessible(closed)) continue;

                var behaviorInterface = kind == HandlerKind.StreamRequest ? "IStreamPipelineBehavior" : "IPipelineBehavior";
                var messageTypes = string.Join(", ", handlerInterface.TypeArguments.Select(FullName));
                registrations.Add(new HandlerRegistration(
                    HandlerKind.DeclaredBehavior,
                    $"global::Mediator.{behaviorInterface}<{messageTypes}>",
                    FullName(closed),
                    FullName(handlerInterface.TypeArguments[0]),
                    handlerInterface.TypeArguments.Length > 1 ? FullName(handlerInterface.TypeArguments[1]) : null,
                    declaration.Order));
            }
        }

        return new HandlerClassResult(new EquatableArray<HandlerRegistration>(registrations.ToImmutable()), null);
    }

    private static void Execute(SourceProductionContext context, ImmutableArray<HandlerClassResult> results, GeneratorSettings settings)
    {
        if (!settings.MediatorReferenced)
        {
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.MediatorNotReferenced, Location.None, " (version 2.1 or later)"));
            return;
        }

        foreach (var diagnostic in settings.Diagnostics)
        {
            context.ReportDiagnostic(diagnostic.ToDiagnostic());
        }

        foreach (var result in results)
        {
            if (result.Diagnostic is not null)
                context.ReportDiagnostic(result.Diagnostic.ToDiagnostic());
        }

        // Handlers first (sorted for deterministic output), then declared behaviors in declaration order:
        // the DI container returns IEnumerable<IPipelineBehavior<,>> in registration order.
        var all = results.SelectMany(r => r.Registrations).Distinct().ToList();
        var registrations = all
            .Where(r => r.Kind != HandlerKind.DeclaredBehavior)
            .OrderBy(r => r.ServiceType, System.StringComparer.Ordinal)
            .ThenBy(r => r.ImplementationType, System.StringComparer.Ordinal)
            .Concat(all
                .Where(r => r.Kind == HandlerKind.DeclaredBehavior)
                .OrderBy(r => r.Order)
                .ThenBy(r => r.ServiceType, System.StringComparer.Ordinal))
            .ToList();

        context.AddSource($"{ClassName}.g.cs", SourceText.From(Emit(settings.Namespace, registrations), Encoding.UTF8));
    }

    private static string Emit(string ns, IReadOnlyList<HandlerRegistration> registrations)
    {
        const string Services = "global::Microsoft.Extensions.DependencyInjection.IServiceCollection";
        const string Descriptor = "global::Microsoft.Extensions.DependencyInjection.ServiceDescriptor";
        const string Registry = "global::Mediator.MediatorRegistry";

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by SwartBerg.Mediator.SourceGenerator. Do not edit.");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// Compile-time SwartBerg.Mediator handler registrations for this project.");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    [global::System.CodeDom.Compiler.GeneratedCode(\"SwartBerg.Mediator.SourceGenerator\", \"1.0.0\")]");
        sb.AppendLine($"    internal static class {ClassName}");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// Registers the mediator infrastructure and every handler in this project without reflection.");
        sb.AppendLine("        /// Trimming and Native AOT safe. Configure options with <c>services.AddMediatorCore(options =&gt; ...)</c>.");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine($"        public static {Services} AddMediatorHandlers(this {Services} services)");
        sb.AppendLine("        {");
        sb.AppendLine("            global::Mediator.MediatorServiceCollectionExtensions.AddMediatorCore(services);");

        if (registrations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"            {Registry}.AddHandlers(services, new {Descriptor}[]");
            sb.AppendLine("            {");
            foreach (var r in registrations)
            {
                sb.AppendLine($"                {Descriptor}.Transient(typeof({r.ServiceType}), typeof({r.ImplementationType})),");
            }
            sb.AppendLine("            });");
            sb.AppendLine();

            var dispatchers = registrations
                .Select(DispatcherRegistration)
                .Where(line => line is not null)
                .Distinct()
                .OrderBy(line => line, System.StringComparer.Ordinal);

            foreach (var line in dispatchers)
            {
                sb.AppendLine($"            {Registry}.{line}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("            return services;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string? DispatcherRegistration(HandlerRegistration r) => r.Kind switch
    {
        HandlerKind.Request => $"RegisterRequest<{r.MessageType}, {r.ResponseType}>();",
        HandlerKind.Command => $"RegisterCommand<{r.MessageType}>();",
        HandlerKind.Notification => $"RegisterNotification<{r.MessageType}>();",
        HandlerKind.StreamRequest => $"RegisterStreamRequest<{r.MessageType}, {r.ResponseType}>();",
        _ => null, // Stream behaviors are resolved by the stream dispatcher; they need no dispatcher of their own.
    };

    private static bool IsHandlerInterface(INamedTypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        return s_handlerInterfaces.ContainsKey(definition.MetadataName)
            && definition.ContainingNamespace is { Name: MediatorNamespace, ContainingNamespace.IsGlobalNamespace: true }
            && definition.ContainingAssembly?.Name == MediatorAssembly;
    }

    private static bool IsOpenGeneric(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.TypeParameters.Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// True when generated code in the same assembly can reference the type (no private or protected nesting).
    /// </summary>
    private static bool IsAccessible(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return IsAccessible(array.ElementType);
            case ITypeParameterSymbol:
                return false;
            case INamedTypeSymbol named:
                for (var current = named; current is not null; current = current.ContainingType)
                {
                    if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected or Accessibility.ProtectedAndInternal)
                        return false;
                }
                return named.TypeArguments.All(IsAccessible);
            default:
                return true;
        }
    }

    private static string FullName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string GetNamespace(Compilation compilation, AnalyzerConfigOptions options)
    {
        options.TryGetValue("build_property.RootNamespace", out var rootNamespace);
        var name = string.IsNullOrWhiteSpace(rootNamespace) ? compilation.AssemblyName : rootNamespace;
        if (string.IsNullOrWhiteSpace(name))
            return "MediatorGenerated";

        var parts = name!.Split('.').Select(SanitizeIdentifier).Where(p => p.Length > 0);
        var result = string.Join(".", parts);
        return result.Length == 0 ? "MediatorGenerated" : result;
    }

    private static string SanitizeIdentifier(string part)
    {
        var sb = new StringBuilder(part.Length + 1);
        foreach (var c in part)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }
        if (sb.Length > 0 && char.IsDigit(sb[0])) sb.Insert(0, '_');

        var identifier = sb.ToString();
        return SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None ? "@" + identifier : identifier;
    }

}
