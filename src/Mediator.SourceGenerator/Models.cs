using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Mediator.SourceGenerator;

internal enum HandlerKind
{
    Request,
    Command,
    Notification,
    StreamRequest,
    StreamBehavior,
    DeclaredBehavior,
}

/// <summary>
/// One handler interface implemented by a class. All members are strings so the model is cache-friendly
/// for the incremental pipeline.
/// </summary>
internal sealed record HandlerRegistration(
    HandlerKind Kind,
    string ServiceType,
    string ImplementationType,
    string MessageType,
    string? ResponseType,
    int Order = -1);

/// <summary>
/// Equatable location used to report diagnostics without holding on to syntax trees.
/// </summary>
internal sealed record LocationInfo(string FilePath, TextSpan TextSpan, LinePositionSpan LineSpan)
{
    public Location ToLocation() => Location.Create(FilePath, TextSpan, LineSpan);

    public static LocationInfo? From(Location? location)
        => location?.SourceTree is null ? null : new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
}

internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, LocationInfo? Location, string Argument)
{
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location?.ToLocation(), Argument);
}

internal sealed record HandlerClassResult(EquatableArray<HandlerRegistration> Registrations, DiagnosticInfo? Diagnostic);

internal sealed record GeneratorSettings(string Namespace, bool MediatorReferenced, EquatableArray<DiagnosticInfo> Diagnostics);

/// <summary>
/// Immutable array with value equality, required for incremental generator caching.
/// </summary>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items) => _items = items;

    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public bool Equals(EquatableArray<T> other) => Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in Items) hash = (hash * 31) + item.GetHashCode();
        return hash;
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
