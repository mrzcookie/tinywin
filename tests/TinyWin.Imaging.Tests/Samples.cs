namespace TinyWin.Imaging.Tests;

/// <summary>Loads the captured DISM output under <c>Samples/</c>. See that directory's README for provenance.</summary>
internal static class Samples
{
    public static string Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Samples", name);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"Missing DISM output sample '{name}'.", path);
    }

    public static string WimInfoList => Load("get-wiminfo-list.txt");

    public static string WimInfoIndex1 => Load("get-wiminfo-index1.txt");

    public static string WimInfoIndex6 => Load("get-wiminfo-index6.txt");

    public static string MountedWimInfo => Load("get-mountedwiminfo.txt");

    public static string ProvisionedAppx => Load("get-provisionedappxpackages.txt");

    public static string Capabilities => Load("get-capabilities.txt");

    public static string Features => Load("get-features.txt");

    public static string Packages => Load("get-packages.txt");

    public static string Error740 => Load("error-740.txt");
}
