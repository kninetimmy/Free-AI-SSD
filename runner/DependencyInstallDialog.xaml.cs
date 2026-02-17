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
    private readonly Dictionary<string, PrereqManifestEntry> _manifestById;
    public DependencyDialogAction Action { get; private set; }
    public IReadOnlyList<PrereqManifestEntry> SelectedEntries =>
        MissingList.Items.OfType<DependencySelectionItem>().Where(i => i.IsSelected).Select(i => i.Entry).ToList();

    public DependencyInstallDialog(IReadOnlyList<MissingDependency> missing, IReadOnlyList<PrereqManifestEntry> manifestEntries)
    {
        InitializeComponent();
        _manifestById = manifestEntries
            .Where(x => !string.IsNullOrWhiteSpace(x.Id))
            .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var catalogById = PrereqCatalog.Tier1.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var selections = missing
            .Where(m => catalogById.ContainsKey(m.Id))
            .Select(m => new DependencySelectionItem(
                m,
                catalogById[m.Id],
                _manifestById.TryGetValue(m.Id, out var manifest) ? manifest : null))
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
        public DependencySelectionItem(MissingDependency missing, PrereqDefinition catalog, PrereqManifestEntry? manifest)
        {
            Missing = missing;
            Catalog = catalog;
            Manifest = manifest;
            Entry = manifest ?? new PrereqManifestEntry
            {
                Id = missing.Id,
                DisplayName = missing.DisplayName,
                Filename = catalog.TargetFileName,
                SilentArgs = catalog.SilentArgs,
                RequiresAdmin = catalog.RequiresAdmin
            };
        }

        public MissingDependency Missing { get; }
        public PrereqDefinition Catalog { get; }
        public PrereqManifestEntry? Manifest { get; }
        public PrereqManifestEntry Entry { get; }
        public bool IsSelected { get; set; } = true;
        public string DisplayLabel =>
            Manifest is null
                ? $"{Missing.DisplayName} (missing from prereqs-manifest.json)"
                : $"{Missing.DisplayName} ({Catalog.TargetFileName})";
    }
}
