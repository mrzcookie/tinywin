using System.Globalization;

namespace TinyWin.Unattend;

/// <summary>
/// Builds the <c>reg.exe</c> command lines used by the <c>RunSynchronous</c> settings.
/// </summary>
/// <remarks>
/// Unattend has no first-class "write this registry value" setting outside of
/// <c>offlineServicing</c>, so every generator in this space shells out to <c>reg.exe</c>.
/// <c>RunSynchronousCommand/Path</c> is handed to CreateProcess directly, not to a shell, so the
/// key is quoted here and the switches are not.
/// </remarks>
internal static class RegistryCommand
{
    internal static string AddDword(string key, string valueName, int data) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"reg.exe add \"{key}\" /v {valueName} /t REG_DWORD /d {data} /f");
}
