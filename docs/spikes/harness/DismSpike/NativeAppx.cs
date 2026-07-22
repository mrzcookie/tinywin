using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Direct P/Invoke to the undocumented dismapi.dll provisioned-appx exports.
/// Deliberately does NOT assume a struct layout: it dumps the returned buffer as
/// qwords, classifies each slot (wide-string pointer / small int / zero), and
/// auto-detects the record stride. The layout in the report is what this printed.
/// </summary>
static unsafe class NativeAppx
{
    const string DISMAPI = "dismapi.dll";
    const string ONLINE_IMAGE = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";

    [DllImport(DISMAPI, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);

    [DllImport(DISMAPI, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);

    [DllImport(DISMAPI, ExactSpelling = true)]
    static extern int DismCloseSession(uint session);

    [DllImport(DISMAPI, ExactSpelling = true)]
    static extern int DismShutdown();

    [DllImport(DISMAPI, ExactSpelling = true)]
    static extern int DismDelete(IntPtr dismStructure);

    // --- the undocumented pair under test ---
    [DllImport(DISMAPI, ExactSpelling = true)]
    static extern int DismGetProvisionedAppxPackages(uint session, out IntPtr packages, out uint count);

    [DllImport(DISMAPI, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int DismRemoveProvisionedAppxPackage(uint session, string packageName);

    // control for the Prelink test: this export does not exist and must fail to bind.
    [DllImport(DISMAPI, ExactSpelling = true)]
    static extern int DismThisDoesNotExist(uint session);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQuery(IntPtr address, out MEMORY_BASIC_INFORMATION buffer, nuint length);

    /// <summary>
    /// dismapi.h wraps all DISM structs in #pragma pack(push, 1). Every field here is 4 or
    /// 8 bytes, so pack(1) and pack(4) produce identical offsets — but pack(8)/natural does
    /// NOT: it would put ResourceId at +0x30 instead of +0x2C and make the stride 72 instead
    /// of 68, silently yielding garbage strings from the second record onward.
    /// Header name is DismAppxPackage; the trailing field is Region (singular) in the header.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    struct DismAppxPackage
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string PackageName;      // +0x00
        [MarshalAs(UnmanagedType.LPWStr)] public string DisplayName;      // +0x08
        [MarshalAs(UnmanagedType.LPWStr)] public string PublisherId;      // +0x10
        public uint MajorVersion;                                          // +0x18
        public uint MinorVersion;                                          // +0x1C
        public uint Build;                                                 // +0x20
        public uint RevisionNumber;                                        // +0x24
        public uint Architecture;                                          // +0x28
        [MarshalAs(UnmanagedType.LPWStr)] public string ResourceId;       // +0x2C
        [MarshalAs(UnmanagedType.LPWStr)] public string InstallLocation;  // +0x34
        [MarshalAs(UnmanagedType.LPWStr)] public string Region;           // +0x3C  (total 68)
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public uint __alignment1;
        public nuint RegionSize;
        public uint State, Protect, Type, __alignment2;
    }

    public static void Run(string[] args)
    {
        Console.WriteLine($"== P/Invoke probe against DISM_ONLINE_IMAGE ==");
        Console.WriteLine($"process: {(Environment.Is64BitProcess ? "x64" : "x86")}  elevated: {IsElevated()}");

        int hr = DismInitialize(2 /* DismLogErrorsWarningsInfo */, null, null);
        Console.WriteLine($"DismInitialize -> 0x{hr:X8}");
        if (hr < 0) { Report(hr); return; }

        try
        {
            hr = DismOpenSession(ONLINE_IMAGE, null, null, out uint session);
            Console.WriteLine($"DismOpenSession(online) -> 0x{hr:X8}  session={session}");
            if (hr < 0) { Report(hr); return; }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                hr = DismGetProvisionedAppxPackages(session, out IntPtr buf, out uint count);
                sw.Stop();
                Console.WriteLine($"DismGetProvisionedAppxPackages -> 0x{hr:X8}  count={count}  ptr=0x{buf:X}  {sw.Elapsed.TotalMilliseconds:F1} ms");
                if (hr < 0) { Report(hr); return; }
                if (count == 0 || buf == IntPtr.Zero) return;

                int stride = Forensics(buf, count);
                Console.WriteLine($"expected stride from dismapi.h pack(1): {Marshal.SizeOf<DismAppxPackage>()} bytes" +
                                  $"  ({(stride == Marshal.SizeOf<DismAppxPackage>() ? "MATCH" : "MISMATCH — investigate")})");

                // Decode with the header-derived struct and cross-check against the forensics.
                Console.WriteLine("\ntyped marshal of first 5 records:");
                for (int i = 0; i < Math.Min(5, count); i++)
                {
                    var r = Marshal.PtrToStructure<DismAppxPackage>(buf + i * Marshal.SizeOf<DismAppxPackage>());
                    Console.WriteLine($"  [{i}] {r.PackageName}");
                    Console.WriteLine($"      display='{r.DisplayName}' publisher='{r.PublisherId}' arch={r.Architecture} " +
                                      $"v{r.MajorVersion}.{r.MinorVersion}.{r.Build}.{r.RevisionNumber}");
                    Console.WriteLine($"      resourceId='{r.ResourceId}' region='{r.Region}'");
                    Console.WriteLine($"      installLocation='{r.InstallLocation}'");
                }

                if (args.Contains("--decode") && stride > 0) Decode(buf, count, stride);

                hr = DismDelete(buf);
                Console.WriteLine($"DismDelete -> 0x{hr:X8}");
            }
            finally { Console.WriteLine($"DismCloseSession -> 0x{DismCloseSession(session):X8}"); }
        }
        finally { Console.WriteLine($"DismShutdown -> 0x{DismShutdown():X8}"); }
    }

    /// <summary>Classify qword slots and auto-detect the repeating record stride.</summary>
    static int Forensics(IntPtr buf, uint count)
    {
        const int MaxSlots = 40;
        var kind = new char[MaxSlots];
        var sample = new string?[MaxSlots];
        var raw = new ulong[MaxSlots];

        for (int i = 0; i < MaxSlots; i++)
        {
            ulong v = *(ulong*)((byte*)buf + i * 8);
            raw[i] = v;
            if (v == 0) { kind[i] = '0'; }
            else if (v < 0x1_0000_0000 && TryWStr((IntPtr)v, out var s1) is false && v < 0x10000) { kind[i] = 'i'; sample[i] = v.ToString(); }
            else if (TryWStr((IntPtr)v, out var s)) { kind[i] = 'S'; sample[i] = s; }
            else if (v < 0x1_0000_0000) { kind[i] = 'i'; sample[i] = v.ToString(); }
            else { kind[i] = '?'; }
        }

        Console.WriteLine();
        Console.WriteLine("qword map of the returned buffer (S=ptr-to-wide-string, i=int, 0=zero, ?=other):");
        for (int i = 0; i < MaxSlots; i++)
            Console.WriteLine($"  +0x{i * 8:X2}  {kind[i]}  {(kind[i] == 'S' ? "\"" + Trunc(sample[i]) + "\"" : kind[i] == 'i' ? sample[i] : $"0x{raw[i]:X16}")}");

        // stride detection: smallest period p (in qwords) >= 2 whose kind pattern repeats
        int strideQ = 0;
        for (int p = 2; p <= MaxSlots / 2; p++)
        {
            bool ok = true;
            for (int i = 0; i + p < MaxSlots && ok; i++) if (kind[i] != kind[i + p]) ok = false;
            if (ok) { strideQ = p; break; }
        }
        Console.WriteLine();
        Console.WriteLine(strideQ > 0
            ? $"detected record stride: {strideQ} qwords = {strideQ * 8} bytes"
            : "stride not detected from the first 40 qwords");
        return strideQ * 8;
    }

    static void Decode(IntPtr buf, uint count, int stride)
    {
        Console.WriteLine();
        Console.WriteLine($"first 5 of {count} records, decoded at stride {stride}:");
        for (int r = 0; r < Math.Min(5, count); r++)
        {
            byte* rec = (byte*)buf + r * stride;
            Console.WriteLine($"  record {r}:");
            for (int i = 0; i < stride / 8; i++)
            {
                ulong v = *(ulong*)(rec + i * 8);
                if (v != 0 && TryWStr((IntPtr)v, out var s)) Console.WriteLine($"    +0x{i * 8:X2} \"{Trunc(s)}\"");
                else Console.WriteLine($"    +0x{i * 8:X2} 0x{v:X}  (lo={unchecked((uint)v)} hi={v >> 32})");
            }
        }
    }

    static bool TryWStr(IntPtr p, out string? s)
    {
        s = null;
        if (p == IntPtr.Zero || (ulong)p < 0x10000) return false;
        if (VirtualQuery(p, out var mbi, (nuint)sizeof(MEMORY_BASIC_INFORMATION)) == 0) return false;
        if (mbi.State != 0x1000 /*MEM_COMMIT*/) return false;
        const uint readable = 0x02 | 0x04 | 0x20 | 0x40 | 0x80;
        if ((mbi.Protect & readable) == 0) return false;
        var sb = new StringBuilder();
        char* c = (char*)p;
        for (int i = 0; i < 512; i++)
        {
            char ch = c[i];
            if (ch == '\0') { s = sb.ToString(); return sb.Length > 0; }
            if (ch < 0x20 || ch > 0x7E) return false; // package metadata is ASCII in practice
            sb.Append(ch);
        }
        return false;
    }

    static string Trunc(string? s) => s is null ? "" : s.Length > 70 ? s[..70] + "…" : s;

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    static void Report(int hr) =>
        Console.WriteLine($"  -> {Marshal.GetExceptionForHR(hr)?.Message ?? "(no message)"}");
}
