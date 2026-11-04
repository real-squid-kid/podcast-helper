using System.Diagnostics;
using System.IO;
using System.Windows;
using PodcastHelper.App.ViewModels;
namespace PodcastHelper.App;
public partial class AboutWindow : Window
{
    public static IReadOnlyList<LicenseItem> ThirdPartyItems { get; } =
    [
        new("Distribution notices", "", "PodcastHelper · bundled components", "THIRD_PARTY_NOTICES.md"),
        new(".NET runtime", "10.0.14 · MIT", ".NET Foundation and Contributors", "licenses/dotnet-runtime/LICENSE.txt"),
        new("Runtime third-party notices", "10.0.14", "Copyright holders listed in the full notices", "licenses/dotnet-runtime/THIRD-PARTY-NOTICES.txt"),
        new("Windows Desktop runtime", "10.0.14 · MIT", ".NET Foundation and Contributors", "licenses/windows-desktop/LICENSE.txt"),
        new("WPF notices", "10.0.14", "Copyright holders listed in the full notices", "licenses/windows-desktop/WPF-NOTICES.txt"),
        new("Windows Forms notices", "10.0.14", "Copyright holders listed in the full notices", "licenses/windows-desktop/WINFORMS-NOTICES.txt")
    ];
    public AboutWindow(AboutViewModel vm)
    {
        InitializeComponent(); DialogSizing.Attach(this); DataContext = vm;
        vm.DiagnosticInfo = ApplicationLocation.DiagnosticInfo; vm.CopyText = Clipboard.SetText;
        vm.OpenLink = uri => { if (uri.IsAbsoluteUri && uri.Scheme == "https") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); };
        vm.ShowLicenses = async thirdParty =>
        {
            IReadOnlyList<LicenseItem> items = thirdParty ? ThirdPartyItems : [new("PodcastHelper", vm.Version + " · MIT", "© 2026 Caspian", "LICENSE")];
            var license = new LicenseViewModel(thirdParty ? "Third-party licenses" : "MIT license", items, vm.ShowMouseHint,
                relative => File.ReadAllTextAsync(Path.Combine(ApplicationLocation.RootDirectory, relative)));
            await license.Loading; new LicenseWindow(license) { Owner = this }.ShowDialog();
        };
        vm.CloseRequested += Close; Closed += (_, _) => vm.CloseRequested -= Close;
    }
}
