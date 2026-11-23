using System.Text.RegularExpressions;

namespace Mediator.Tests
{
    /// <summary>
    /// Guards the CI workflows: runners stay on a pinned, current image, and CI installs every framework the library
    /// ships for. A floating <c>*-latest</c> label moves on the vendor's schedule, so CI would change without a commit.
    /// </summary>
    public class CiWorkflowTests
    {
        // Oldest Ubuntu runner image a workflow may use. Raise it when moving to a newer GA image.
        private static readonly Version MinimumUbuntu = new(26, 4);

        [Fact]
        public void Workflows_ShouldUsePinnedRunnerImages_NoOlderThanTheMinimum()
        {
            var problems = new List<string>();
            foreach (var (file, text) in Workflows())
            {
                var runsOn = Regex.Matches(text, @"^\s*runs-on:\s*(\S+)", RegexOptions.Multiline);
                runsOn.Should().NotBeEmpty($"{file} should declare runs-on");

                foreach (Match match in runsOn)
                {
                    var label = match.Groups[1].Value.Trim('\'', '"');
                    if (label.EndsWith("-latest", StringComparison.Ordinal))
                    {
                        problems.Add($"{file}: '{label}' floats; pin an explicit image version");
                        continue;
                    }

                    var ubuntu = Regex.Match(label, @"^ubuntu-(\d+)\.(\d+)");
                    if (ubuntu.Success)
                    {
                        var version = new Version(int.Parse(ubuntu.Groups[1].Value), int.Parse(ubuntu.Groups[2].Value));
                        if (version < MinimumUbuntu)
                        {
                            problems.Add($"{file}: '{label}' is older than ubuntu-{MinimumUbuntu.Major}.{MinimumUbuntu.Minor:00}");
                        }
                    }
                }
            }

            problems.Should().BeEmpty();
        }

        [Fact]
        public void Workflows_ShouldInstallEveryFrameworkTheLibraryTargets()
        {
            var csproj = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Mediator", "Mediator.csproj"));
            var frameworks = Regex.Match(csproj, "<TargetFrameworks>([^<]+)</TargetFrameworks>").Groups[1].Value
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            frameworks.Should().NotBeEmpty();

            foreach (var (file, text) in Workflows())
            {
                foreach (var tfm in frameworks)
                {
                    var channel = Regex.Match(tfm, @"^net(\d+)\.0$").Groups[1].Value + ".0.x";
                    text.Should().Contain(channel, $"{file} must install the SDK/runtime for {tfm}");
                }
            }
        }

        private static IEnumerable<(string File, string Text)> Workflows()
        {
            var directory = Path.Combine(RepositoryRoot(), ".github", "workflows");
            var files = Directory.GetFiles(directory, "*.yml").Concat(Directory.GetFiles(directory, "*.yaml")).ToArray();
            files.Should().NotBeEmpty();
            return files.Select(f => (Path.GetFileName(f), File.ReadAllText(f)));
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mediator.sln")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("Repository root (Mediator.sln) not found.");
        }
    }
}
