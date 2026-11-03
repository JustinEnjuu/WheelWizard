using System.Runtime.InteropServices;

namespace WheelWizard.Recomp;

/// <summary>Platform support and published setup asset names for WiiCompiled.</summary>
public static class RecompPlatform
{
    private static readonly System.IO.Abstractions.IPath NativePath = new Testably.Abstractions.RealFileSystem().Path;

    /// <summary>
    /// Whether this is a Linux build on an architecture the recomp publishes an AppImage for. The AppImage
    /// is always run unpacked (see <see cref="RecompProcessRunner"/>), so this holds inside a Flatpak
    /// sandbox too: the AppImage then installs into the sandbox's own XDG data directory.
    /// </summary>
    public static bool IsLinux { get; } = OperatingSystem.IsLinux() && LinuxReleaseAssetName(RuntimeInformation.OSArchitecture) is not null;

    /// <summary>
    /// Whether this is a macOS build. macOS downloads <see cref="MacReleaseAssetName"/>, a package containing the native macOS setup script and tools for both architectures.
    /// </summary>
    public static bool IsMacOS { get; } = OperatingSystem.IsMacOS();

    /// <summary>Whether this platform uses backend-owned product and user-data folders.</summary>
    public static bool UsesSubcommandSetup => IsLinux || IsMacOS;

    public static bool IsSupported => OperatingSystem.IsWindows() || UsesSubcommandSetup;

    /// <summary>The macOS installer package published by the recomp packaging workflow.</summary>
    public const string MacReleaseAssetName = "WiiCompiled-Setup.pkg";

    /// <summary>The name of the installed host copy inside Wheel Wizard's recomp install directory.</summary>
    public static string SetupFileName =>
        IsLinux ? "WiiCompiled-Setup.AppImage"
        : IsMacOS ? MacReleaseAssetName
        : RecompSetupCommandBuilder.SetupFileName;

    /// <summary>The release asset to download on this machine.</summary>
    public static string ReleaseAssetName => IsLinux ? LinuxReleaseAssetName(RuntimeInformation.OSArchitecture)! : SetupFileName;

    /// <summary>
    /// The AppImage the recomp publishes for a Linux architecture, or <see langword="null"/> when it
    /// publishes none. The names are fixed by the recomp's packaging workflow.
    /// </summary>
    public static string? LinuxReleaseAssetName(Architecture architecture) =>
        architecture switch
        {
            Architecture.X64 => "WiiCompiled-Setup-x86_64.AppImage",
            Architecture.Arm64 => "WiiCompiled-Setup-aarch64.AppImage",
            _ => null,
        };

    /// <summary>The file name a downloaded release is cached under; the tag keeps releases apart.</summary>
    public static string CachedSetupFileName(string tagName)
    {
        var sanitized = new string(tagName.Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray());
        return $"WiiCompiled-Setup-{sanitized}{NativePath.GetExtension(SetupFileName)}";
    }

    /// <summary>The pattern that matches every cached setup of this platform, for pruning superseded ones.</summary>
    public static string CachedSetupSearchPattern => $"WiiCompiled-Setup-*{NativePath.GetExtension(SetupFileName)}";
}
