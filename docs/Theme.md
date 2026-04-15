# Theme

The Free AI SSD WPF apps (Prep-App and Runner) share a single neumorphic dark
theme with cyan → magenta → purple neon accents. All tokens live in
`shared/UI/Theme/` and are merged into each host assembly via pack URIs in
`App.xaml`:

```xml
<ResourceDictionary.MergedDictionaries>
    <ResourceDictionary Source="pack://application:,,,/UI/Theme/Theme.xaml"/>
</ResourceDictionary.MergedDictionaries>
```

`Theme.xaml` is just an aggregator that pulls in the four token dictionaries
in dependency order.

## Files

| File | Contains |
| --- | --- |
| `Colors.xaml` | `Color` + `SolidColorBrush` tokens, gradient brushes |
| `Shadows.xaml` | `DropShadowEffect` resources (raised / sunken / focus glows) |
| `Typography.xaml` | Font families and the `Body/Caption/H1/H2/Neon…Text` styles |
| `Controls.xaml` | All themed control styles (buttons, expanders, toggles, text box, combo box, log viewer, tooltip, progress bar, spotlight ring) |
| `Theme.xaml` | Merges the above four |
| `LedStatusIndicator.xaml(.cs)` | Reusable 10×10 status lamp UserControl |
| `ReducedMotion.cs` | OS-accessibility gate for animations |

The shared csproj (`FreeAiSsd.Shared.csproj`) targets plain `net8.0` so it can
*not* compile WPF types. Each WPF host (`prep-app`, `runner`) therefore
`Link`s these files in as Pages / Compile items. When adding a new themed
file under `shared/UI/Theme/`, remember to add the same `<Page>` or `<Compile>`
entry to **both** host csprojs.

## Tokens

### Surfaces

| Token (…Color / …Brush) | Hex | Usage |
| --- | --- | --- |
| `BgBase` | `#1A1D24` | Window background |
| `BgRaised` | `#1F232C` | Cards that sit above base |
| `BgSunken` | `#15181E` | Insets (log viewer, text input wells) |
| `BgElevated` | `#252934` | Top-most surfaces (dialogs, tooltips) |
| `SurfaceShadow` | `#0B0D12` | Drop-shadow color under raised surfaces |
| `SurfaceBorder` | `#222630` | 1px hairline on cards |

### Text

All four levels pass WCAG 2.1 AA (4.5:1) on every surface token.

| Token | Hex | Usage |
| --- | --- | --- |
| `TextPrimary` | `#EAEEF6` | Body copy, headings |
| `TextSecondary` | `#B6BDCC` | Sub-headings, secondary labels |
| `TextMuted` | `#8F95A8` | Captions, timestamps (**do not darken** — guarded for AA on `BgElevated`) |
| `TextDisabled` | `#555A68` | Disabled controls only (does not meet 4.5:1 by design) |

### Accents

| Token | Hex | Usage |
| --- | --- | --- |
| `AccentCyan` | `#00E5FF` | Primary action, focus rings, LED Busy |
| `AccentMagenta` | `#FF2D92` | Destructive / high-energy action |
| `AccentPurple` | `#8A2BE2` | Gradient companion to cyan/magenta |

### Status

| Token | Hex | Meaning |
| --- | --- | --- |
| `StatusSuccess` | `#4CE0B3` | Green LED, completed badges |
| `StatusWarning` | `#FFB454` | Amber LED, warnings |
| `StatusDanger` | `#FF4D6D` | Red LED, errors |
| `StatusInfo` | `#5BC0FF` | Info banner accent |

### Gradients

`NeonGradientBrush` (cyan → magenta → purple), `NeonHotGradientBrush`
(magenta → purple), `FocusBorderGradientBrush` (cyan → magenta), plus
`RaisedSurfaceGradientBrush` / `SunkenSurfaceGradientBrush` for 3D fills.

### Shadows

* `RaisedHighlightShadow` + `RaisedDarkShadow` — pair these on a `Border` to
  fake a neumorphic extrude (light from top-left).
* `RaisedDarkShadowSmall` — lighter-weight extrude for buttons.
* `SunkenDarkShadow` — single inset shadow for wells.
* `FocusGlowCyan` / `FocusGlowMagenta` — keyboard-focus ring.
* `HoverGlowCyan` — hover affordance.

## Motion & accessibility

### Reduced motion

`ReducedMotion.Apply(Application)` is called from both apps' `OnStartup`.
It reads `SystemParameters.ClientAreaAnimation` (the Windows *Show animations*
toggle) and, when the user has opted out:

1. Replaces `ButtonPressTransform` in `Application.Resources` with a frozen
   `TranslateTransform(0, 0)` — the 1px press nudge on buttons becomes a no-op.
2. Sets `ReducedMotion.IsEnabled = true` so `LedStatusIndicator` can default
   its `AllowPulse` property to `false`, suppressing the LED pulse Storyboard
   via the `State=Busy AND AllowPulse=True` MultiDataTrigger.

All button styles bind their pressed-state translate via
`{DynamicResource ButtonPressTransform}` (never `StaticResource`) so the
startup override actually propagates.

### Keyboard focus

Every interactive style has an explicit `IsKeyboardFocused` trigger that
applies `FocusGlowCyan` (or a gradient border, for toggle switches). Never
rely on `IsMouseOver` alone for focus affordance.

## Adding a new themed control

1. **Put the Style in `Controls.xaml`** with an `x:Key`. Use
   `{StaticResource …}` for color / font / shadow tokens defined above.
2. **Hit-test the four states**: default, hover (`IsMouseOver`), keyboard
   focus (`IsKeyboardFocused`), pressed (`IsPressed`) or checked
   (`IsChecked`). Each needs a distinct visual.
3. **For press feedback**, bind `RenderTransform` to
   `{DynamicResource ButtonPressTransform}` inside the `IsPressed`/`IsChecked`
   trigger — don't inline a new `TranslateTransform` — so reduced motion
   works.
4. **For focus affordance**, set `Effect="{StaticResource FocusGlowCyan}"` (or
   a gradient border) in the `IsKeyboardFocused` trigger.
5. **Contrast**: if you add a new text color or background, verify ≥ 4.5:1
   against every surface it can land on. `TextMuted` is the current lower
   bound; do not go below it for any readable text.
6. **If you add a new shared XAML / code-behind file**, add matching
   `<Page>` / `<Compile>` linked entries to both
   `prep-app/FreeAiSsd.PrepApp.csproj` and
   `runner/FreeAiSsd.Runner.csproj` — the shared csproj can't host WPF types.

### Minimal example

```xml
<Style x:Key="MyPillButton" TargetType="{x:Type Button}">
    <Setter Property="Foreground" Value="{StaticResource TextPrimaryBrush}"/>
    <Setter Property="Background" Value="{StaticResource BgRaisedBrush}"/>
    <Setter Property="BorderBrush" Value="{StaticResource SurfaceBorderBrush}"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="Padding" Value="12,6"/>
    <Setter Property="FontFamily" Value="{StaticResource PrimaryFontFamily}"/>
    <Setter Property="Template">
        <Setter.Value>
            <ControlTemplate TargetType="{x:Type Button}">
                <Border x:Name="root"
                        Background="{TemplateBinding Background}"
                        BorderBrush="{TemplateBinding BorderBrush}"
                        BorderThickness="{TemplateBinding BorderThickness}"
                        CornerRadius="14"
                        Effect="{StaticResource RaisedDarkShadowSmall}">
                    <ContentPresenter HorizontalAlignment="Center"
                                      VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter TargetName="root" Property="Effect"
                                Value="{StaticResource HoverGlowCyan}"/>
                    </Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True">
                        <Setter TargetName="root" Property="Effect"
                                Value="{StaticResource FocusGlowCyan}"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter TargetName="root" Property="RenderTransform"
                                Value="{DynamicResource ButtonPressTransform}"/>
                    </Trigger>
                </ControlTemplate.Triggers>
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>
```

That's it — the new button automatically respects reduced-motion, has a
visible keyboard-focus state, and uses the shared palette.
