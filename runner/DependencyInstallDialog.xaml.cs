using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner;

public enum DependencyDialogAction
{
    None,
    Install,
    Skip,
    CancelExit
}

public partial class DependencyInstallDialog : System.Windows.Window
{
    private readonly List<PrereqManifestEntry> _catalog;
    public DependencyDialogAction Action { get; private set; }
    public IReadOnlyList<PrereqManifestEntry> SelectedEntries =>
        MissingList.Items.OfType<DependencySelectionItem>().Where(i => i.IsSelected).Select(i => i.Entry).ToList();

    public DependencyInstallDialog(IReadOnlyList<MissingDependency> missing, IReadOnlyList<PrereqManifestEntry> catalog)
    {
        InitializeComponent();
        _catalog = catalog.ToList();

        var selections = missing
            .Select(m => new DependencySelectionItem(
                m,
                _catalog.FirstOrDefault(c => string.Equals(c.Id, m.Id, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        MissingList.ItemsSource = selections;
    }

    private void InstallSelected_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Action = DependencyDialogAction.Install;
        DialogResult = true;
    }

    private void Skip_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Action = DependencyDialogAction.Skip;
        DialogResult = true;
    }

    private void CancelExit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Action = DependencyDialogAction.CancelExit;
        DialogResult = false;
    }

    private sealed class DependencySelectionItem
    {
        public DependencySelectionItem(MissingDependency missing, PrereqManifestEntry? entry)
        {
            Missing = missing;
            Entry = entry ?? new PrereqManifestEntry
            {
                Id = missing.Id,
                DisplayName = missing.DisplayName,
                Filename = string.Empty,
                SilentArgs = string.Empty,
                RequiresAdmin = missing.RequiresAdmin
            };
        }

        public MissingDependency Missing { get; }
        public PrereqManifestEntry Entry { get; }
        public bool IsSelected { get; set; } = true;
        public string DisplayLabel =>
            Entry.Filename.Length > 0
                ? $"{Missing.DisplayName} ({Entry.Filename})"
                : $"{Missing.DisplayName} (installer not found in manifest)";
    }
}
