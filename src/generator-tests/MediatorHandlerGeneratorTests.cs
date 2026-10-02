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
    public void Declared_behaviors_are_closed_per_request_honouring_constraints_and_order()
    {
        var (output, diagnostics, generated) = Run(WithAssemblyAttribute(
            "[assembly: Mediator.MediatorPipelineBehaviors(typeof(App.Outer<,>), typeof(App.CommandsOnly<,>), typeof(App.ClassResponses<,>), typeof(App.Swapped<,>), typeof(App.StreamLog<,>))]") + """

            public interface ICommandMarker;
            public sealed record CreateUser(string Name) : IRequest<System.Guid>, ICommandMarker;
            public sealed class CreateUserHandler : IRequestHandler<CreateUser, System.Guid>
            {
                public Task<System.Guid> Handle(CreateUser request, CancellationToken cancellationToken) => Task.FromResult(System.Guid.Empty);
            }
            public sealed record Count : IStreamRequest<int>;
            public sealed class CountHandler : IStreamRequestHandler<Count, int>
            {
                public async IAsyncEnumerable<int> Handle(Count request, CancellationToken cancellationToken) { yield return 1; await Task.Yield(); }
            }

            public sealed class Outer<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            public sealed class CommandsOnly<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>, ICommandMarker
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            public sealed class ClassResponses<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse> where TResponse : class
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            public sealed class Swapped<TResponse, TRequest> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            public sealed class StreamLog<TRequest, TResponse> : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : IStreamRequest<TResponse>
            {
                public IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            """);

        diagnostics.Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        const string Ping = "global::Mediator.IPipelineBehavior<global::App.Ping, string>";
        const string Create = "global::Mediator.IPipelineBehavior<global::App.CreateUser, global::System.Guid>";

        // Value-type response (Guid): closed at compile time, so no open-generic closing is needed at runtime.
        Registered(generated, Create).Should().Equal(
            "global::App.Outer<global::App.CreateUser, global::System.Guid>",
            "global::App.CommandsOnly<global::App.CreateUser, global::System.Guid>",
            "global::App.Swapped<global::System.Guid, global::App.CreateUser>");

        // Ping is not an ICommandMarker, and string satisfies "class".
        Registered(generated, Ping).Should().Equal(
            "global::App.Outer<global::App.Ping, string>",
            "global::App.ClassResponses<global::App.Ping, string>",
            "global::App.Swapped<string, global::App.Ping>");

        Registered(generated, "global::Mediator.IStreamPipelineBehavior<global::App.Count, int>")
            .Should().Equal("global::App.StreamLog<global::App.Count, int>");
    }

    [Fact]
    public void Declared_command_behaviors_are_closed_over_requests_without_response()
    {
        var (output, diagnostics, generated) = Run(WithAssemblyAttribute(
            "[assembly: Mediator.MediatorPipelineBehaviors(typeof(App.CommandLog<>), typeof(App.Outer<,>))]") + """

            public sealed record Save(string Name) : IRequest;
            public sealed class SaveHandler : IRequestHandler<Save>
            {
                public Task Handle(Save request, CancellationToken cancellationToken) => Task.CompletedTask;
            }
            public sealed class CommandLog<TRequest> : IPipelineBehavior<TRequest> where TRequest : IRequest
            {
                public Task Handle(TRequest request, RequestHandlerDelegate next, CancellationToken cancellationToken) => next();
            }
            public sealed class Outer<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : IRequest<TResponse>
            {
                public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
            }
            """);

        diagnostics.Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        // Command behaviors apply to IRequest only; request behaviors apply to IRequest<T> only.
        Registered(generated, "global::Mediator.IPipelineBehavior<global::App.Save>").Should().Equal("global::App.CommandLog<global::App.Save>");
        Registered(generated, "global::Mediator.IPipelineBehavior<global::App.Ping, string>").Should().Equal("global::App.Outer<global::App.Ping, string>");
    }

    [Fact]
    public void Invalid_declared_behavior_reports_MEDGEN004()
    {
        var (output, diagnostics, generated) = Run(WithAssemblyAttribute(
            "[assembly: Mediator.MediatorPipelineBehaviors(typeof(App.PingHandler), typeof(System.Collections.Generic.List<>))]"));

        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        diagnostics.Where(d => d.Id == "MEDGEN004").Should().HaveCount(2);
        generated.Should().NotContain("IPipelineBehavior");
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

    /// <summary>The shared messages with an assembly attribute placed after the using directives.</summary>
    private static string WithAssemblyAttribute(string attribute)
        => Messages.Replace("namespace App;", attribute + "\n\nnamespace App;");

    /// <summary>Implementation types registered for a service type, in registration order.</summary>
    private static List<string> Registered(string generated, string serviceType)
    {
        var prefix = $"Transient(typeof({serviceType}), typeof(";
        return generated.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains(prefix, StringComparison.Ordinal))
            .Select(line => line.Substring(line.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length).TrimEnd(')', ',', ' ').TrimEnd(')'))
            .ToList();
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
