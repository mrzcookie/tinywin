using System.Xml.Linq;

namespace TinyWin.Unattend;

/// <summary>
/// Names, namespaces and attribute values from the Windows unattend schema.
/// </summary>
/// <remarks>
/// Every value here is load-bearing. Windows Setup matches components by the exact
/// <c>name</c>/<c>processorArchitecture</c>/<c>publicKeyToken</c>/<c>language</c>/<c>versionScope</c>
/// tuple; a component whose architecture does not match the media is silently ignored, which is
/// exactly the "it failed confusingly at install time" failure this project exists to avoid.
/// </remarks>
internal static class UnattendSchema
{
    internal static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";

    /// <summary>Windows Configuration Manager namespace, source of <c>wcm:action</c>.</summary>
    internal static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>The Microsoft public key token every in-box unattend component is signed with.</summary>
    internal const string PublicKeyToken = "31bf3856ad364e35";

    internal const string Language = "neutral";
    internal const string VersionScope = "nonSxS";

    // Configuration passes. Only the three we emit are listed.
    internal const string WindowsPePass = "windowsPE";
    internal const string SpecializePass = "specialize";
    internal const string OobeSystemPass = "oobeSystem";

    // Components. Note the -WinPE suffix on the international component: the plain
    // Microsoft-Windows-International-Core is not valid in the windowsPE pass and vice versa.
    internal const string InternationalCoreWinPe = "Microsoft-Windows-International-Core-WinPE";
    internal const string InternationalCore = "Microsoft-Windows-International-Core";
    internal const string Setup = "Microsoft-Windows-Setup";
    internal const string Deployment = "Microsoft-Windows-Deployment";
    internal const string ShellSetup = "Microsoft-Windows-Shell-Setup";

    /// <summary>
    /// <c>processorArchitecture</c> for x64 media. Spelled <c>amd64</c>, never <c>x64</c>.
    /// </summary>
    internal const string Amd64 = "amd64";

    /// <summary>
    /// <c>processorArchitecture</c> for Arm64 media. Spelled <c>arm64</c>, never <c>aarch64</c>
    /// and — unlike the ADK's older <c>arm</c> value — not 32-bit.
    /// </summary>
    internal const string Arm64 = "arm64";
}
