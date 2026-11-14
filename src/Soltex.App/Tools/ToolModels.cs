namespace Soltex.App.Settings;

/// <summary>Zone layouts for snapping windows (FancyZones-class). Off: no hook runs.</summary>
public enum WindowZonesLayout { Off, TwoColumns, ThreeColumns, Grid, Focus }

/// <summary>One key remap: <see cref="From"/> acts as <see cref="To"/> (a virtual-key code; 0 turns the key off).</summary>
public sealed record KeyRemap(int From, int To);
