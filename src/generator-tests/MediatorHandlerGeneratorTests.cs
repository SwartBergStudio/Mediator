using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Mediator.SourceGenerator.Tests;

public class MediatorHandlerGeneratorTests
{
    private const string Messages = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Mediator;

        namespace App;

        public sealed record Ping(string Text) : IRequest<string>;
        public sealed class PingHandler : IRequestHandler<Ping, string>
        {
            public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Text);
        }
        """;

    [Fact]
    public void Generates_registrations_and_dispatchers_that_compile()
    {
        var (output, diagnostics, generated) = Run(Messages + """

            public sealed class DoIt : IRequest;
            internal sealed class DoItHandler : IRequestHandler<DoIt>
            {
                public Task Handle(DoIt request, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class Happened : INotification;
            public sealed class FirstHandler : INotificationHandler<Happened>
            {
                public Task Handle(Happened notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class SecondHandler : INotificationHandler<Happened>
            {
                public Task Handle(Happened notification, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class Count : IStreamRequest<int>;
            public sealed class CountHandler : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count request, CancellationToken cancellationToken) { yield return 1; await Task.Yield(); }
            }

            public abstract class BaseHandler : IRequestHandler<Ping, string>
            {
                public abstract Task<string> Handle(Ping request, CancellationToken cancellationToken);
            }
            """);

        diagnostics.Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        generated.Should().Contain("namespace App.Root");
        generated.Should().Contain("typeof(global::Mediator.IRequestHandler<global::App.Ping, string>), typeof(global::App.PingHandler)");
        generated.Should().Contain("typeof(global::Mediator.IRequestHandler<global::App.DoIt>), typeof(global::App.DoItHandler)");
        generated.Should().Contain("typeof(global::App.FirstHandler)").And.Contain("typeof(global::App.SecondHandler)");
        generated.Should().Contain("RegisterRequest<global::App.Ping, string>();");
        generated.Should().Contain("RegisterCommand<global::App.DoIt>();");
        generated.Should().Contain("RegisterStreamRequest<global::App.Count, int>();");
        generated.Should().NotContain("BaseHandler");
        CountOccurrences(generated, "RegisterNotification<global::App.Happened>();").Should().Be(1);
    }

    [Fact]
    public void Private_nested_handler_reports_MEDGEN002_and_is_skipped()
    {
        var (_, diagnostics, generated) = Run(Messages + """

            public class Outer
            {
                private sealed class Hidden : IRequestHandler<Ping, string>
                {
                    public Task<string> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult("");
                }
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "MEDGEN002");
        generated.Should().NotContain("Hidden");
    }

    [Fact]
    public void Open_generic_behavior_reports_MEDGEN003_and_is_skipped()
    {
        var (_, diagnostics, generated) = Run(Messages + """

            public sealed class Logging<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse>
                where TRequest : IStreamRequest<TResponse>
            {
                public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            """);

        diagnostics.Should().ContainSingle(d => d.Id == "MEDGEN003");
        generated.Should().NotContain("Logging");
    }

    [Fact]
    public void Partial_handler_is_registered_once()
    {
        var (_, _, generated) = Run(Messages.Replace("public sealed class PingHandler", "public sealed partial class PingHandler") + """

            public sealed partial class PingHandler : System.IDisposable { public void Dispose() { } }
            """);

        CountOccurrences(generated, "typeof(global::App.PingHandler)").Should().Be(1);
    }

    [Fact]
    public void Missing_mediator_reference_reports_MEDGEN001()
    {
        var compilation = CSharpCompilation.Create("NoMediator",
            new[] { CSharpSyntaxTree.ParseText("namespace X; public class C { }") },
            BasicReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(new MediatorHandlerGenerator()).RunGenerators(compilation);

        driver.GetRunResult().Diagnostics.Should().ContainSingle(d => d.Id == "MEDGEN001");
        driver.GetRunResult().GeneratedTrees.Should().BeEmpty();
    }

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics, string Generated) Run(string source)
    {
        var references = BasicReferences()
            .Add(MetadataReference.CreateFromFile(typeof(IMediator).Assembly.Location))
            .Add(MetadataReference.CreateFromFile(typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));

        var compilation = CSharpCompilation.Create("App",
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new MediatorHandlerGenerator().AsSourceGenerator() },
            optionsProvider: new TestOptionsProvider("App.Root"),
            parseOptions: new CSharpParseOptions(LanguageVersion.Latest));

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var generated = driver.GetRunResult().GeneratedTrees.Single().ToString();
        return (output, diagnostics, generated);
    }

    private static ImmutableArray<MetadataReference> BasicReferences()
        => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) is "netstandard.dll" or "mscorlib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToImmutableArray();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
