using System.Globalization;

namespace Soltex.Audio.UI;

/// <summary>How device names are shown: repeated names are numbered, never labelled with internal IDs.</summary>
public static class DeviceNames
{
    /// <summary>"" for the first item with a name (or a unique name), then " (2)", " (3)"… in list order.</summary>
    public static string DuplicateSuffix<T>(IReadOnlyList<T> items, T item, Func<T, string> name)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        string wanted = name(item);
        int count = 0, index = -1;
        foreach (T other in items)
        {
            if (!string.Equals(name(other), wanted, StringComparison.Ordinal)) continue;
            if (EqualityComparer<T>.Default.Equals(other, item)) index = count;
            count++;
        }
        return count < 2 || index < 1 ? "" : " (" + (index + 1).ToString(CultureInfo.CurrentCulture) + ")";
    }

    /// <summary>
    /// The name without the driver or hardware in brackets ("Headphones (USB Audio Interface)" → "Headphones"), for a
    /// narrow box whose tooltip and list show the full name. A name that is all brackets is kept.
    /// </summary>
    public static string WithoutHardware(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int bracket = name.IndexOf(" (", StringComparison.Ordinal);
        return bracket > 0 ? name[..bracket].TrimEnd() : name;
    }
}
