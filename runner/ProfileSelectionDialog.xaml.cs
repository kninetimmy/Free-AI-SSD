using FreeAiSsd.Shared;

namespace FreeAiSsd.Runner;

public partial class ProfileSelectionDialog : System.Windows.Window
{
    private UserProfile? _selection;
    private readonly bool _isRequired;

    public UserProfile? SelectedProfile => _selection;

    public ProfileSelectionDialog(bool isRequired = false)
    {
        _isRequired = isRequired;
        InitializeComponent();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isRequired && _selection is null)
            e.Cancel = true;

        base.OnClosing(e);
    }

    private void FlightSimCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Select(UserProfile.FlightSim);

    private void GeneralAssistantCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        Select(UserProfile.GeneralAssistant);

    private void Card_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
        {
            if (sender == FlightSimCard) Select(UserProfile.FlightSim);
            else if (sender == GeneralAssistantCard) Select(UserProfile.GeneralAssistant);
        }
    }

    private void Continue_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selection is not null)
            DialogResult = true;
    }

    private void Select(UserProfile profile)
    {
        _selection = profile;
        ContinueButton.IsEnabled = true;
        ApplyCardState(FlightSimCard, profile == UserProfile.FlightSim);
        ApplyCardState(GeneralAssistantCard, profile == UserProfile.GeneralAssistant);
    }

    private static void ApplyCardState(System.Windows.Controls.Border card, bool selected)
    {
        var resources = System.Windows.Application.Current.Resources;
        if (selected)
        {
            card.BorderBrush = (System.Windows.Media.Brush)resources["FocusBorderGradientBrush"];
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (System.Windows.Media.Color)resources["AccentCyanColor"],
                ShadowDepth = 0,
                BlurRadius = 20,
                Opacity = 0.75
            };
        }
        else
        {
            card.BorderBrush = (System.Windows.Media.Brush)resources["SurfaceBorderBrush"];
            card.Effect = (System.Windows.Media.Effects.Effect)resources["RaisedDarkShadow"];
        }
    }
}
