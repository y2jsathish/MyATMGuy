using System.Reflection;
using NetArchTest.Rules;

namespace ATM.Architecture.Tests;

/// <summary>
/// Enforces the Clean Architecture dependency rules from the blueprint (section 4).
/// Dependencies point inward: Core references nothing, Application references only Core,
/// Infrastructure and Presentation never reference each other or the UI.
/// </summary>
public class LayerDependencyTests
{
    private static readonly Assembly Core = typeof(ATM.Core.ValueObjects.TerminalId).Assembly;
    private static readonly Assembly ApplicationLayer = typeof(ATM.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(ATM.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Presentation = typeof(ATM.Presentation.DependencyInjection).Assembly;

    private static readonly string[] ThirdPartyInfrastructure =
    [
        "Microsoft.EntityFrameworkCore",
        "Serilog",
        "Polly",
        "System.Data.SQLite",
        "Microsoft.Data.Sqlite",
    ];

    private static void ShouldNotDependOn(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        result.IsSuccessful.Should().BeTrue(
            "{0} must not depend on {1}. Offending types: {2}",
            assembly.GetName().Name,
            string.Join(", ", forbidden),
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Core_DependsOnNoOtherLayerOrFramework()
    {
        ShouldNotDependOn(Core, ["ATM.Application", "ATM.Infrastructure", "ATM.Presentation", "ATM.UI", "ATM.Host", .. ThirdPartyInfrastructure, "System.Windows", "Microsoft.Extensions"]);
    }

    [Fact]
    public void Core_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Core.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().OnlyContain(n => n.StartsWith("System", StringComparison.Ordinal) || n == "netstandard" || n == "mscorlib");
    }

    [Fact]
    public void Application_DependsOnlyOnCore()
    {
        ShouldNotDependOn(ApplicationLayer, ["ATM.Infrastructure", "ATM.Presentation", "ATM.UI", "ATM.Host", .. ThirdPartyInfrastructure, "System.Windows"]);
    }

    [Fact]
    public void Infrastructure_DoesNotDependOnPresentationOrUi()
    {
        ShouldNotDependOn(Infrastructure, "ATM.Presentation", "ATM.UI", "ATM.Host", "System.Windows");
    }

    [Fact]
    public void Presentation_DoesNotDependOnInfrastructureOrWpf()
    {
        ShouldNotDependOn(Presentation, "ATM.Infrastructure", "ATM.UI", "ATM.Host", "System.Windows", "PresentationFramework");
    }

    [Fact]
    public void Presentation_ReferencesNoWpfAssemblies()
    {
        var references = Presentation.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        references.Should().NotContain(n => n.StartsWith("Presentation", StringComparison.Ordinal) || n == "WindowsBase" || n == "System.Xaml");
    }

    [Fact]
    public void DeviceAdapters_LiveOnlyInInfrastructure()
    {
        var implementations = Types.InAssemblies([Core, ApplicationLayer, Presentation])
            .That().AreClasses().And().ImplementInterface(typeof(ATM.Application.Devices.IDevicePort))
            .GetTypes();

        implementations.Should().BeEmpty("device adapters belong in ATM.Infrastructure so vendor SDKs stay out of the inner layers");
    }

    [Fact]
    public void ViewModels_AreSealedAndNamedConsistently()
    {
        var result = Types.InAssembly(Presentation)
            .That().Inherit(typeof(ATM.Presentation.Navigation.ViewModelBase))
            .Should().HaveNameEndingWith("ViewModel")
            .And().BeSealed()
            .GetResult();

        result.IsSuccessful.Should().BeTrue("offending types: {0}", string.Join(", ", result.FailingTypeNames ?? []));
    }
}
