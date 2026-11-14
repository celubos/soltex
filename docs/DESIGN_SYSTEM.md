# Design system

Soltex uses WPF's Fluent theme for control behaviour and its own palette, called Studio, for colour. All theme files
live in [`src/Soltex.App/Themes`](../src/Soltex.App/Themes).

## Files

| File | Contents |
| --- | --- |
| `Tokens.xaml` | Every colour literal in the app, plus radius, spacing, type and motion scales. No other XAML file may contain a colour literal. |
| `Studio.Dark.xaml`, `Studio.Light.xaml` | Turn the Studio colours into `Studio.*` brushes and re-point the shared Fluent keys (`CardBackgroundFillColorDefaultBrush`, `TextFillColorPrimaryBrush`, `AccentFillColorDefaultBrush`, the `SystemFillColor*BackgroundBrush` set, and so on) so standard controls follow the palette. |
| `Studio.HighContrast.xaml` | Maps every `Studio.*` brush to a Windows system colour. Fluent keys are left to Fluent's own high-contrast handling. |
| `Studio.Controls.xaml` | Keyed Studio control styles: `Studio.PrimaryButton`, `Studio.SecondaryButton`, `Studio.GhostButton`, `Studio.Switch`, `Studio.Fader`, `Studio.Chip`, `Studio.SegmentItem`, `Studio.TogglePill`, `Studio.Row`, `Studio.Hero`, `Studio.Eyebrow`, `Studio.CompactCombo`, the shared `Studio.FocusVisual`, and an `AccentButtonStyle` override. |
| `Shell.xaml` | Shell layout and text styles: `Shell.Card`, `Shell.PageTitle`, `Shell.PageSubtitle`, `Shell.SectionTitle`, `Shell.CardTitle`, `Shell.Body`, `Shell.Secondary`, `Shell.Metric`, `Shell.Glyph`, `Shell.NavItem`, `Shell.InfoBar`, `Shell.SettingRow`, `Shell.Divider`, and layout values (`Shell.PagePadding`, `Shell.CardPadding`, `Shell.CardGap`, `Shell.RowGap`, `Shell.NavItemHeight`). |

## How a theme is applied

`App.xaml` merges `Tokens.xaml` and `Shell.xaml`. At start-up and whenever a setting changes,
`Shell/ThemeService.cs`:

1. sets the Fluent `ThemeMode` (Light, Dark or System);
2. writes the density-dependent layout values into the `Shell.*` keys (Comfortable or Compact);
3. removes any merged `Studio.*` dictionary and appends the palette (`Studio.Dark`, `Studio.Light`, or
   `Studio.HighContrast` when Windows high contrast is on), then `Studio.Controls`. Appending last lets the palette win
   over the Fluent theme dictionary;
4. turns animations off when the user picks reduced motion or Windows has client-area animation disabled.

Text and control size (100–200 %) is a `LayoutTransform` on the window content, so everything scales together.

Brushes are consumed with `DynamicResource`, so switching between light and dark restyles open windows without a
restart.

## Studio palette

Dark is the default look: deep ink surfaces, cool text, a violet accent with a cyan secondary. Light uses paper
surfaces, ink text and the deeper accent variants for contrast.

| Role | Dark | Light |
| --- | --- | --- |
| Base | `StudioDarkBase` `#0A0C11` | `StudioLightBase` `#F3F4F9` |
| Rail | `StudioDarkRail` `#0D1016` | `StudioLightRail` `#ECEEF5` |
| Card | `StudioDarkCard` `#131720` | `StudioLightCard` `#FFFFFF` |
| Raised | `StudioDarkRaised` `#1B2130` | `StudioLightRaised` `#F6F7FB` |
| Control | `StudioDarkControl` `#1F2534` | `StudioLightControl` `#EDEFF5` |
| Stroke | `StudioDarkStroke` `#232A38` | `StudioLightStroke` `#E1E4ED` |
| Text | `StudioDarkText` `#EEF1F7` | `StudioLightText` `#0E1220` |
| Secondary text | `StudioDarkTextSecondary` `#A2ABBD` | `StudioLightTextSecondary` `#4A5367` |
| Tertiary text | `StudioDarkTextTertiary` `#6B7588` | `StudioLightTextTertiary` `#7A8396` |

Shared accent and status colours:

| Token | Value | Use |
| --- | --- | --- |
| `StudioAccent` | `#8B5CF6` | Primary actions, selection |
| `StudioAccentBright` / `StudioAccentDeep` | `#A78BFA` / `#6D28D9` | Accent text on dark / filled buttons and light-theme text |
| `StudioAccentCyan` | `#22D3EE` | Decorative gradients (rules, rings, meters) only |
| `StudioSuccess` | `#34D399` | Confirmed-good state |
| `StudioCaution` | `#FBBF24` | Needs attention |
| `StudioCritical` | `#F87171` | Danger |

Each status colour has a `…Deep` variant for text on light surfaces and a tinted surface colour per theme
(`Studio{Dark,Light}{Success,Caution,Critical,Info}Surface`).

Audio channels have fixed identity colours used by meters and strips: Microphone `#F472B6`, Game `#FB923C`, Chat
`#34D399`, Media `#38BDF8`, Aux `#A78BFA`, Master `#E2E8F0`.

## Scales

Defined in `Tokens.xaml`:

- **Radius:** `RadiusXs` 4, `RadiusSm` 8, `RadiusMd` 10, `RadiusLg` 12, `RadiusCard` 14, `RadiusHero` 18,
  `RadiusHeroXl` 24. Fluent controls use `ControlCornerRadius` 8 and `OverlayCornerRadius` 12 from the palette files.
- **Spacing:** `Space2xs` 4, `SpaceXs` 6, `SpaceSm` 8, `SpaceMd` 12, `SpaceLg` 16, `SpaceXl` 20, `Space2xl` 24,
  `Space3xl` 28, plus padding thicknesses `PadCard`, `PadHero`, `PadCompact`, `PadPill`, `PadField`, `PadButton`.
- **Type:** Segoe UI Variable for text and display (`Studio.TextFont`, `Studio.DisplayFont`), Cascadia Mono for
  numbers and identifiers (`MonoFont`). Size tokens run from `FontMicro` 9 to `FontDisplay` 38. Prefer a named text
  style over an inline `FontSize`.
- **Motion:** `MotionFast` 140 ms, `MotionBase` 180 ms, `MotionSlow` 220 ms. Check `ThemeService.AnimationsEnabled`
  before animating.

## Older audio palette

Sections 1–6 of `Tokens.xaml` also hold an earlier warm graphite palette (`CanvasColor`, `SurfaceColor`, `TextColor`,
`AccentColor` and their `…Brush` forms). The audio views in `src/Soltex.Audio.UI` still use it through
`Soltex.Audio.UI/Themes/Audio.xaml`, which links `Tokens.xaml`. New shell work should use the Studio and `Shell.*`
resources instead.

## Rules

- Colour lives only in `Tokens.xaml`. `eng/verify-design-tokens.ps1` scans the XAML under `src/Soltex.App`,
  `src/Soltex.Audio.App` and `src/Soltex.Audio.UI` and fails on any other colour literal. CI runs it.
- Add a colour as a token, give it a brush in each palette file (and a system-colour mapping in
  `Studio.HighContrast.xaml`), then reference the brush.
- Status is never colour alone: pair it with a word or icon.
- Every interactive control has visible rest, hover, pressed, keyboard-focus and disabled states. Focus uses
  `Studio.FocusVisual`.
- Keep it light: no third-party control library and no runtime colour computation.
