using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace Mediator.SourceGenerator;

/// <summary>
/// An open generic behavior listed in <c>[assembly: MediatorPipelineBehaviors(...)]</c>.
/// </summary>
/// <param name="Type">The open generic behavior type.</param>
/// <param name="TargetKind">Request (IPipelineBehavior) or StreamRequest (IStreamPipelineBehavior).</param>
/// <param name="Positions">For each of the behavior's type parameters, its position in the behavior interface.</param>
/// <param name="Order">Position in the attribute: the execution order.</param>
internal sealed record BehaviorDeclaration(INamedTypeSymbol Type, HandlerKind TargetKind, ImmutableArray<int> Positions, int Order);

/// <summary>
/// Reads and validates <c>MediatorPipelineBehaviorsAttribute</c> and closes declared behaviors over request types,
/// honouring their generic constraints.
/// </summary>
internal static class BehaviorDeclarations
{
    private const string AttributeName = "Mediator.MediatorPipelineBehaviorsAttribute";

    private static readonly ConditionalWeakTable<Compilation, Result> s_cache = new();

    internal sealed class Result
    {
        public Result(ImmutableArray<BehaviorDeclaration> declarations, ImmutableArray<DiagnosticInfo> diagnostics)
        {
            Declarations = declarations;
            Diagnostics = diagnostics;
        }

        public ImmutableArray<BehaviorDeclaration> Declarations { get; }
        public ImmutableArray<DiagnosticInfo> Diagnostics { get; }
    }

    public static Result Read(Compilation compilation) => s_cache.GetValue(compilation, ReadCore);

    private static Result ReadCore(Compilation compilation)
    {
        var attributeType = compilation.GetTypeByMetadataName(AttributeName);
        var attribute = attributeType is null
            ? null
            : compilation.Assembly.GetAttributes().FirstOrDefault(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attributeType));

        if (attribute is null || attribute.ConstructorArguments.Length != 1)
            return new Result(ImmutableArray<BehaviorDeclaration>.Empty, ImmutableArray<DiagnosticInfo>.Empty);

        var location = LocationInfo.From(attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation());
        var declarations = ImmutableArray.CreateBuilder<BehaviorDeclaration>();
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();

        var values = attribute.ConstructorArguments[0].Kind == TypedConstantKind.Array
            ? attribute.ConstructorArguments[0].Values
            : ImmutableArray.Create(attribute.ConstructorArguments[0]);

        for (var order = 0; order < values.Length; order++)
        {
            if (values[order].Value is INamedTypeSymbol type && TryCreate(type, order, out var declaration))
            {
                declarations.Add(declaration);
            }
            else
            {
                var name = (values[order].Value as ITypeSymbol)?.ToDisplayString() ?? "null";
                diagnostics.Add(new DiagnosticInfo(Mediator.SourceGenerator.Diagnostics.InvalidDeclaredBehavior, location, name));
            }
        }

        return new Result(declarations.ToImmutable(), diagnostics.ToImmutable());
    }

    private static bool TryCreate(INamedTypeSymbol type, int order, out BehaviorDeclaration declaration)
    {
        declaration = null!;
        var definition = type.OriginalDefinition;
        if (definition.IsAbstract || definition.TypeParameters.Length != 2 || definition.ContainingType is { IsGenericType: true })
            return false;

        foreach (var candidate in definition.AllInterfaces)
        {
            var kind = candidate.OriginalDefinition.MetadataName switch
            {
                "IPipelineBehavior`2" => HandlerKind.Request,
                "IStreamPipelineBehavior`2" => HandlerKind.StreamRequest,
                _ => (HandlerKind?)null,
            };
            if (kind is null || candidate.OriginalDefinition.ContainingNamespace?.ToDisplayString() != "Mediator")
                continue;

            var positions = definition.TypeParameters
                .Select(p => candidate.TypeArguments.IndexOf(p, SymbolEqualityComparer.Default))
                .ToImmutableArray();
            if (positions.Any(p => p < 0))
                continue;

            declaration = new BehaviorDeclaration(definition, kind.Value, positions, order);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Closes the behavior over (request, response). Returns null when its generic constraints reject the pair.
    /// </summary>
    public static INamedTypeSymbol? TryClose(BehaviorDeclaration declaration, ImmutableArray<ITypeSymbol> messageTypes, Compilation compilation)
    {
        var arguments = declaration.Positions.Select(p => messageTypes[p]).ToArray();
        var map = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        for (var i = 0; i < arguments.Length; i++)
        {
            map[declaration.Type.TypeParameters[i]] = arguments[i];
        }

        for (var i = 0; i < arguments.Length; i++)
        {
            if (!Satisfies(declaration.Type.TypeParameters[i], arguments[i], map, compilation))
                return null;
        }

        return declaration.Type.Construct(arguments);
    }

    private static bool Satisfies(ITypeParameterSymbol parameter, ITypeSymbol argument, Dictionary<ITypeParameterSymbol, ITypeSymbol> map, Compilation compilation)
    {
        if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType) return false;
        if (parameter.HasValueTypeConstraint && (!argument.IsValueType || IsNullableValueType(argument))) return false;
        if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType) return false;
        if (parameter.HasConstructorConstraint && !HasPublicParameterlessConstructor(argument)) return false;

        foreach (var constraint in parameter.ConstraintTypes)
        {
            var target = Substitute(constraint, map, compilation);
            if (target is null) return false;
            var conversion = ((Microsoft.CodeAnalysis.CSharp.CSharpCompilation)compilation).ClassifyConversion(argument, target);
            if (!(conversion.IsIdentity || conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing)))
                return false;
        }

        return true;
    }

    private static ITypeSymbol? Substitute(ITypeSymbol type, Dictionary<ITypeParameterSymbol, ITypeSymbol> map, Compilation compilation)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                return map.TryGetValue(parameter, out var mapped) ? mapped : null;
            case IArrayTypeSymbol array:
                var element = Substitute(array.ElementType, map, compilation);
                return element is null ? null : compilation.CreateArrayTypeSymbol(element, array.Rank);
            case INamedTypeSymbol named when named.IsGenericType:
                var arguments = new ITypeSymbol[named.TypeArguments.Length];
                for (var i = 0; i < arguments.Length; i++)
                {
                    var substituted = Substitute(named.TypeArguments[i], map, compilation);
                    if (substituted is null) return null;
                    arguments[i] = substituted;
                }
                return named.OriginalDefinition.Construct(arguments);
            default:
                return type;
        }
    }

    private static bool IsNullableValueType(ITypeSymbol type)
        => type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;

    private static bool HasPublicParameterlessConstructor(ITypeSymbol type)
    {
        if (type.IsValueType) return true;
        return type is INamedTypeSymbol { IsAbstract: false } named
            && named.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
    }
}
