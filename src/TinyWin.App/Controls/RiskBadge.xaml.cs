using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TinyWin.Catalog.Models;

namespace TinyWin.App.Controls;

/// <summary>
/// The colour-coded risk chip shown on every component row.
/// </summary>
/// <remarks>
/// Brushes are applied in code rather than through a value converter so that they can be re-applied
/// on <see cref="FrameworkElement.ActualThemeChanged"/>. A converter returns a brush resolved once,
/// which leaves the badges wrong the moment the system flips between light and dark — and following
/// the system theme is a requirement, not a nicety (docs/PLAN.md section 4).
/// </remarks>
public sealed partial class RiskBadge : UserControl
{
    public static readonly DependencyProperty RiskProperty = DependencyProperty.Register(
        nameof(Risk),
        typeof(RiskTier),
        typeof(RiskBadge),
        new PropertyMetadata(RiskTier.Safe, (d, _) => ((RiskBadge)d).Apply()));

    public RiskBadge()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => Apply();
        Loaded += (_, _) => Apply();
    }

    public RiskTier Risk
    {
        get => (RiskTier)GetValue(RiskProperty);
        set => SetValue(RiskProperty, value);
    }

    private void Apply()
    {
        Label.Text = Risk switch
        {
            RiskTier.Safe => "Safe",
            RiskTier.Caution => "Caution",
            RiskTier.Breaking => "Breaking",
            RiskTier.Unserviceable => "Unserviceable",
            _ => Risk.ToString(),
        };

        // Unserviceable is filled rather than tinted. It is the only tier that produces an image
        // that cannot be repaired, and it should not read as "critical, but like the other one".
        var (background, foreground, border) = Risk switch
        {
            RiskTier.Safe => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", "SystemFillColorSuccessBrush"),
            RiskTier.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", "SystemFillColorCautionBrush"),
            RiskTier.Breaking => ("SystemFillColorCriticalBackgroundBrush", "SystemFillColorCriticalBrush", "SystemFillColorCriticalBrush"),
            _ => ("SystemFillColorCriticalBrush", "TextOnAccentFillColorPrimaryBrush", "SystemFillColorCriticalBrush"),
        };

        Chip.Background = Lookup(background);
        Chip.BorderBrush = Lookup(border);
        Label.Foreground = Lookup(foreground);

        ToolTipService.SetToolTip(this, Risk switch
        {
            RiskTier.Safe => "Removable with no functional loss a typical user would notice.",
            RiskTier.Caution => "Loses a feature some people rely on, but the OS is unaffected.",
            RiskTier.Breaking => "Known to break unrelated software or a core workflow. Opt-in only.",
            _ => "Leaves an image Microsoft will not service and which cannot be repaired in place. "
                 + "Requires a typed confirmation on the Review page.",
        });
    }

    private static Brush? Lookup(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
