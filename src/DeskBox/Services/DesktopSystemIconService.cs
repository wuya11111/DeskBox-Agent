using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DeskBox.Services;

internal static class DesktopSystemIconService
{
    private const string HideDesktopIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    private static readonly IReadOnlyDictionary<string, (string ParsingName, string DesktopIconId, string DefaultName)> Entries =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["this_pc"] = ("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "This PC"),
            ["recycle_bin"] = ("::{645FF040-5081-101B-9F08-00AA002F954E}", "{645FF040-5081-101B-9F08-00AA002F954E}", "Recycle Bin"),
            ["network"] = ("::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Network"),
            ["control_panel"] = ("::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "Control Panel"),
            ["user_files"] = ("::{59031a47-3f72-44a7-89c5-5595fe6b30ee}", "{59031a47-3f72-44a7-89c5-5595fe6b30ee}", "User Files")
        };

    internal static bool TryResolve(
        string systemId,
        out string normalizedId,
        out string parsingName,
        out string desktopIconId,
        out string defaultName)
    {
        if (Entries.TryGetValue(systemId.Trim(), out var entry))
        {
            normalizedId = systemId.Trim().ToLowerInvariant();
            parsingName = entry.ParsingName;
            desktopIconId = entry.DesktopIconId;
            defaultName = entry.DefaultName;
            return true;
        }

        normalizedId = string.Empty;
        parsingName = string.Empty;
        desktopIconId = string.Empty;
        defaultName = string.Empty;
        return false;
    }

    internal static void SetDesktopIconHidden(string desktopIconId, bool hidden)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(HideDesktopIconsKey);
        foreach (string subKeyName in new[] { "NewStartPanel", "ClassicStartMenu" })
        {
            using RegistryKey subKey = key.CreateSubKey(subKeyName);
            if (hidden)
            {
                subKey.SetValue(desktopIconId, 1, RegistryValueKind.DWord);
            }
            else
            {
                subKey.DeleteValue(desktopIconId, throwOnMissingValue: false);
            }
        }

        SendMessageTimeout(
            new IntPtr(0xFFFF),
            WmSettingChange,
            IntPtr.Zero,
            "ShellState",
            SmtoAbortIfHung,
            1000,
            out _);
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        string lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        uint eventId,
        uint flags,
        IntPtr item1,
        IntPtr item2);
}
