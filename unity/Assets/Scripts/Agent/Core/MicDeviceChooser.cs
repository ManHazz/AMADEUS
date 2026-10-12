using System;

/// <summary>Pure logic for picking a microphone from the device list and a saved (name based) choice.</summary>
public static class MicDeviceChooser
{
    /// <summary>Index of the device to use: exact saved name, then case-insensitive, then 0. -1 if the list is empty.</summary>
    public static int Choose(string[] devices, string saved)
    {
        if (devices == null || devices.Length == 0) return -1;
        if (!string.IsNullOrEmpty(saved))
        {
            for (int i = 0; i < devices.Length; i++) if (devices[i] == saved) return i;
            for (int i = 0; i < devices.Length; i++)
                if (string.Equals(devices[i], saved, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }

    public static int Next(int index, int count) { return count <= 0 ? -1 : (index + 1 + count) % count; }

    /// <summary>Stable text signature of a device list, used to detect hot-plug changes.</summary>
    public static string Signature(string[] devices)
    {
        return devices == null ? "" : string.Join("\n", devices);
    }

    public static bool SameList(string[] a, string[] b) { return Signature(a) == Signature(b); }
}
