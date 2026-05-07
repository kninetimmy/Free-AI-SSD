using System.Xml.Linq;

public sealed class MacPlatformBoundaryTests
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    [Fact]
    public void SharedProject_RemainsPlainNet8Project()
    {
        var project = LoadProject("shared", "FreeAiSsd.Shared.csproj");
        var root = project.Root ?? throw new InvalidOperationException("Project XML has no root.");

        Assert.Equal("Microsoft.NET.Sdk", root.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.False(IsTrueProperty(project, "UseWPF"));
        Assert.False(IsTrueProperty(project, "UseWindowsForms"));
        Assert.False(IsTrueProperty(project, "EnableWindowsTargeting"));
    }

    [Fact]
    public void SharedProject_KnownWindowsOnlyPackagesAreExplicitDebtAndDoNotGrow()
    {
        var packages = PackageReferences(LoadProject("shared", "FreeAiSsd.Shared.csproj"));
        var knownWindowsOnlyDebt = new HashSet<string>(Comparer)
        {
            "System.Management",
            "NAudio",
            "SharpDX.DirectInput",
        };

        Assert.True(knownWindowsOnlyDebt.IsSubsetOf(packages),
            "The MAC2 audit expects these legacy shared-package blockers until they are moved behind platform adapters.");

        var blockedWindowsOnlyPackages = new HashSet<string>(knownWindowsOnlyDebt, Comparer)
        {
            "System.Speech",
            "Microsoft.Windows.Compatibility",
            "SharpDX.XInput",
        };

        var unapproved = packages
            .Where(p => blockedWindowsOnlyPackages.Contains(p) && !knownWindowsOnlyDebt.Contains(p))
            .OrderBy(p => p, Comparer)
            .ToArray();

        Assert.Empty(unapproved);
    }

    [Fact]
    public void RunnerCli_RemainsPortableHttpClient()
    {
        var project = LoadProject("runner-cli", "FreeAiSsd.RunnerCli.csproj");
        var packages = PackageReferences(project);
        var references = ProjectReferences(project);

        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(packages, p => p.Contains("NAudio", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packages, p => p.Contains("SharpDX", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(packages, p => p.Contains("System.Speech", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, IsRunnerProjectReference);
    }

    [Fact]
    public void RunnerCore_RemainsPlainPortableProject()
    {
        var project = LoadProject("runner-core", "FreeAiSsd.RunnerCore.csproj");
        var root = project.Root ?? throw new InvalidOperationException("Project XML has no root.");
        var packages = PackageReferences(project);
        var references = ProjectReferences(project);

        Assert.Equal("Microsoft.NET.Sdk", root.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.False(IsTrueProperty(project, "UseWPF"));
        Assert.False(IsTrueProperty(project, "UseWindowsForms"));
        Assert.False(IsTrueProperty(project, "EnableWindowsTargeting"));
        Assert.DoesNotContain(packages, IsBlockedWindowsOnlyPackage);
        Assert.DoesNotContain(references, IsRunnerProjectReference);
    }

    [Fact]
    public void RunnerProject_ReferencesRunnerCore()
    {
        var references = ProjectReferences(LoadProject("runner", "FreeAiSsd.Runner.csproj"));

        Assert.Contains(references, reference =>
            reference.Replace('\\', '/').Equals("../runner-core/FreeAiSsd.RunnerCore.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PrepCore_RemainsPlainPortableProject()
    {
        // MAC16: prep-core/ holds platform-neutral PrepApp business logic
        // (ArtifactStaging, Prereq, OllamaPackage, Model, Readiness,
        // Encryption services + ModelOperations + StarterModelCatalog +
        // MacArtifactAvailability + OllamaServerHandle). Must build plain
        // net8.0 so a future macOS PrepApp host (MAC17) can consume it
        // without dragging WPF into the SwiftUI bundle's net8.0 sidecar.
        var project = LoadProject("prep-core", "FreeAiSsd.PrepCore.csproj");
        var root = project.Root ?? throw new InvalidOperationException("Project XML has no root.");
        var packages = PackageReferences(project);
        var references = ProjectReferences(project);

        Assert.Equal("Microsoft.NET.Sdk", root.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.False(IsTrueProperty(project, "UseWPF"));
        Assert.False(IsTrueProperty(project, "UseWindowsForms"));
        Assert.False(IsTrueProperty(project, "EnableWindowsTargeting"));
        Assert.DoesNotContain(packages, IsBlockedWindowsOnlyPackage);
        Assert.DoesNotContain(references, IsPrepAppProjectReference);
        Assert.DoesNotContain(references, IsRunnerProjectReference);
    }

    [Fact]
    public void PrepAppProject_ReferencesPrepCore()
    {
        var references = ProjectReferences(LoadProject("prep-app", "FreeAiSsd.PrepApp.csproj"));

        Assert.Contains(references, reference =>
            reference.Replace('\\', '/').Equals("../prep-core/FreeAiSsd.PrepCore.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MacRunnerHost_RemainsPlainNet8WithoutWindowsPackages()
    {
        // MAC6 sidecar: net8.0 host process spawned by the Swift mac-runner.
        // Allowed to depend on RunnerCore + Shared + the ASP.NET Core
        // framework; must not target Windows or pull in Windows-only NuGet
        // packages, otherwise it cannot publish for osx-arm64.
        var project = LoadProject("mac-runner-host", "FreeAiSsd.MacRunnerHost.csproj");
        var root = project.Root ?? throw new InvalidOperationException("Project XML has no root.");
        var packages = PackageReferences(project);
        var references = ProjectReferences(project);

        Assert.Equal("Microsoft.NET.Sdk", root.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.False(IsTrueProperty(project, "UseWPF"));
        Assert.False(IsTrueProperty(project, "UseWindowsForms"));
        Assert.False(IsTrueProperty(project, "EnableWindowsTargeting"));
        Assert.DoesNotContain(packages, IsBlockedWindowsOnlyPackage);
        Assert.DoesNotContain(references, IsRunnerProjectReference);

        // Must reference runner-core (the whole point of MAC6 — reuse
        // RunnerLocalApiService byte-for-byte rather than fork it in Swift).
        Assert.Contains(references, reference =>
            reference.Replace('\\', '/').Equals("../runner-core/FreeAiSsd.RunnerCore.csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MacPrepHost_RemainsPlainNet8WithoutWindowsPackages()
    {
        // MAC17 sidecar: net8.0 host process spawned by the Swift
        // mac-prep-app. Allowed to depend on prep-core + Shared (no ASP.NET
        // Core — prep is one-shot stdin commands, not a long-running HTTP
        // host). Must not target Windows or pull Windows-only NuGet packages,
        // otherwise it cannot publish for osx-arm64.
        var project = LoadProject("mac-prep-host", "FreeAiSsd.MacPrepHost.csproj");
        var root = project.Root ?? throw new InvalidOperationException("Project XML has no root.");
        var packages = PackageReferences(project);
        var references = ProjectReferences(project);

        Assert.Equal("Microsoft.NET.Sdk", root.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", RequiredProperty(project, "TargetFramework"));
        Assert.DoesNotContain("windows", RequiredProperty(project, "TargetFramework"), StringComparison.OrdinalIgnoreCase);
        Assert.False(IsTrueProperty(project, "UseWPF"));
        Assert.False(IsTrueProperty(project, "UseWindowsForms"));
        Assert.False(IsTrueProperty(project, "EnableWindowsTargeting"));
        Assert.DoesNotContain(packages, IsBlockedWindowsOnlyPackage);
        Assert.DoesNotContain(references, IsRunnerProjectReference);
        Assert.DoesNotContain(references, IsPrepAppProjectReference);

        // Must reference prep-core (the whole point of MAC17 — reuse
        // platform-neutral PrepApp services rather than fork them in Swift).
        Assert.Contains(references, reference =>
            reference.Replace('\\', '/').Equals("../prep-core/FreeAiSsd.PrepCore.csproj", StringComparison.OrdinalIgnoreCase));

        // Symmetric guardrail: prep-host must NOT pull in runner-core
        // (different lifecycle, different command surface). MAC17 keeps
        // the Mac sidecars cleanly separated.
        Assert.DoesNotContain(references, reference =>
            reference.Replace('\\', '/').Contains("runner-core", StringComparison.OrdinalIgnoreCase));
    }

    private static XDocument LoadProject(params string[] pathParts)
    {
        return XDocument.Load(Path.Combine(FindRepoRoot(), Path.Combine(pathParts)));
    }

    private static IReadOnlySet<string> PackageReferences(XDocument project)
    {
        return project.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToHashSet(Comparer);
    }

    private static IReadOnlyList<string> ProjectReferences(XDocument project)
    {
        return project.Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")?.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToArray();
    }

    private static string RequiredProperty(XDocument project, string propertyName)
    {
        return OptionalProperty(project, propertyName)
            ?? throw new InvalidOperationException($"Missing required project property '{propertyName}'.");
    }

    private static string? OptionalProperty(XDocument project, string propertyName)
    {
        return project.Descendants(propertyName).Select(e => e.Value).FirstOrDefault();
    }

    private static bool IsTrueProperty(XDocument project, string propertyName)
    {
        return string.Equals(OptionalProperty(project, propertyName), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRunnerProjectReference(string reference)
    {
        var normalized = reference.Replace('\\', '/');
        return normalized.Contains("/runner/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("../runner/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrepAppProjectReference(string reference)
    {
        var normalized = reference.Replace('\\', '/');
        return normalized.Contains("/prep-app/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("../prep-app/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedWindowsOnlyPackage(string package)
    {
        return package.Contains("NAudio", StringComparison.OrdinalIgnoreCase)
            || package.Contains("SharpDX", StringComparison.OrdinalIgnoreCase)
            || package.Contains("System.Speech", StringComparison.OrdinalIgnoreCase)
            || package.Contains("System.Management", StringComparison.OrdinalIgnoreCase)
            || package.Contains("Microsoft.Windows.Compatibility", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FreeAiSsd.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find FreeAiSsd.sln from the test output directory.");
    }
}
