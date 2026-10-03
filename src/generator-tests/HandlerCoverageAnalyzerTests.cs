using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Mediator.SourceGenerator.Tests;

public class HandlerCoverageAnalyzerTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Mediator;
        namespace App;

        """;

    [Fact]
    public async Task Request_without_handler_reports_MEDGEN005_at_the_request()
    {
        var diagnostics = await Analyze(Usings + """
            public sealed record Orphan : IRequest<int>;
            public sealed record OrphanCommand : IRequest;
            public sealed record OrphanStream : IStreamRequest<int>;
            """);

        diagnostics.Should().HaveCount(3).And.OnlyContain(d => d.Id == "MEDGEN005");
        diagnostics.Select(d => d.GetMessage()).Should().Contain(m => m.Contains("'Orphan'"))
            .And.Contain(m => m.Contains("'OrphanCommand'"))
            .And.Contain(m => m.Contains("'OrphanStream'"));
        diagnostics.Should().OnlyContain(d => d.Location.IsInSource);
    }

    [Fact]
    public async Task Request_with_two_handlers_reports_MEDGEN006()
    {
        var diagnostics = await Analyze(Usings + """
            public sealed record Ping : IRequest<string>;
            public sealed class FirstPingHandler : IRequestHandler<Ping, string>
            {
                public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("1");
            }
            public sealed class SecondPingHandler : IRequestHandler<Ping, string>
            {
                public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("2");
            }
            """);

        diagnostics.Should().ContainSingle().Which.Id.Should().Be("MEDGEN006");
        diagnostics[0].GetMessage().Should().Contain("App.FirstPingHandler").And.Contain("App.SecondPingHandler").And.Contain("2 handlers");
    }

    [Fact]
    public async Task Handled_requests_and_unhandled_notifications_report_nothing()
    {
        var diagnostics = await Analyze(Usings + """
            public sealed record Ping : IRequest<string>;
            public sealed class PingHandler : IRequestHandler<Ping, string>
            {
                public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("pong");
            }
            public sealed record Save : IRequest;
            internal sealed class SaveHandler : IRequestHandler<Save>
            {
                public Task Handle(Save request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed record Count : IStreamRequest<int>;
            public sealed class CountHandler : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count request, CancellationToken cancellationToken) { yield return 1; await Task.Yield(); }
            }
            public sealed record NobodyListens : INotification;
            public abstract record AbstractRequest : IRequest<int>;
            public sealed record Generic<T> : IRequest<T>;
            """);

        diagnostics.Should().BeEmpty("notifications may have no handlers, and abstract or open generic requests are not checked");
    }

    [Fact]
    public async Task Pragma_suppresses_MEDGEN005_for_a_request_handled_elsewhere()
    {
        var diagnostics = await Analyze(Usings + """
            #pragma warning disable MEDGEN005 // handled in another project
            public sealed record HandledElsewhere : IRequest<int>;
            #pragma warning restore MEDGEN005
            """);

        diagnostics.Should().BeEmpty();
    }

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .Append(MetadataReference.CreateFromFile(typeof(IMediator).Assembly.Location));

        var compilation = CSharpCompilation.Create("App",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var diagnostics = await compilation
            .WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new HandlerCoverageAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();
        return diagnostics.Where(d => !d.IsSuppressed).ToImmutableArray();
    }
}
