using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;

/// <summary>
/// Non-elevated static analysis:
///  (a) Marshal.Prelink every dismapi.dll P/Invoke — proves the exports resolve by name
///      in this process without opening a DISM session.
///  (b) For every internal native struct in Microsoft.Dism, rebuild an identical struct
///      with natural (Pack=0) alignment and diff the field offsets against the declared
///      Pack=4 layout. Any struct that differs is one where the library disagrees with
///      what an MSVC x64 compiler would emit for a header with no #pragma pack.
/// </summary>
static class Layout
{
    public static void Run()
    {
        Prelink();
        Console.WriteLine();
        PackDiff();
    }

    static void Prelink()
    {
        Console.WriteLine("=== Marshal.Prelink: do the dismapi.dll exports resolve? ===");
        Console.WriteLine("(binds the DLL + entry point; does NOT call the function, needs no elevation)\n");

        foreach (var name in new[]
                 {
                     "DismInitialize", "DismOpenSession", "DismCloseSession", "DismShutdown",
                     "DismDelete", "DismGetProvisionedAppxPackages", "DismRemoveProvisionedAppxPackage",
                 })
        {
            var mi = typeof(NativeAppx).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
            if (mi is null) { Console.WriteLine($"  {name,-38} (no managed stub)"); continue; }
            try { Marshal.Prelink(mi); Console.WriteLine($"  {name,-38} RESOLVED"); }
            catch (Exception ex) { Console.WriteLine($"  {name,-38} FAILED: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Also prove a deliberately bogus export fails, so "RESOLVED" above means something.
        try
        {
            var mi = typeof(NativeAppx).GetMethod("DismThisDoesNotExist", BindingFlags.NonPublic | BindingFlags.Static);
            if (mi is not null) { Marshal.Prelink(mi); Console.WriteLine($"  {"DismThisDoesNotExist",-38} RESOLVED  <-- control failed!"); }
        }
        catch (Exception ex) { Console.WriteLine($"  {"DismThisDoesNotExist",-38} FAILED (control ok): {ex.GetType().Name}"); }
    }

    static void PackDiff()
    {
        Console.WriteLine("=== declared Pack=4 layout vs natural x64 alignment ===\n");
        var asm = typeof(Microsoft.Dism.DismApi).Assembly;
        var structs = asm.GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum && t.StructLayoutAttribute is { Pack: 4 })
            .OrderBy(t => t.Name);

        var mismatched = new List<string>();

        foreach (var t in structs)
        {
            Type natural;
            try { natural = Rebuild(t, pack: 0); }
            catch (Exception ex) { Console.WriteLine($"  SKIP     {t.Name,-24} ({ex.GetType().Name})"); continue; }
            var fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            int declSize = Try(() => Marshal.SizeOf(t));
            int natSize = Try(() => Marshal.SizeOf(natural));

            var diffs = new List<string>();
            foreach (var f in fields)
            {
                int a = Try(() => (int)Marshal.OffsetOf(t, f.Name));
                int b = Try(() => (int)Marshal.OffsetOf(natural, f.Name));
                if (a != b) diffs.Add($"      {f.Name,-18} declared +0x{a:X2}  natural +0x{b:X2}");
            }

            if (diffs.Count == 0 && declSize == natSize)
            {
                Console.WriteLine($"  OK       {t.Name,-24} size {declSize} (identical under both packings)");
            }
            else
            {
                mismatched.Add(t.Name);
                Console.WriteLine($"  MISMATCH {t.Name,-24} declared size {declSize}, natural size {natSize}");
                foreach (var d in diffs) Console.WriteLine(d);
            }
        }

        Console.WriteLine();
        Console.WriteLine(mismatched.Count == 0
            ? "No struct is affected by Pack=4 on x64."
            : $"Affected by Pack=4 on x64: {string.Join(", ", mismatched)}");
    }

    static int Try(Func<int> f) { try { return f(); } catch { return -1; } }

    /// <summary>Emit a runtime clone of the struct with a different Pack.</summary>
    static Type Rebuild(Type src, int pack)
    {
        var ab = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("LayoutProbe"), AssemblyBuilderAccess.Run);
        var mb = ab.DefineDynamicModule("m");
        var tb = mb.DefineType("N_" + src.Name,
            TypeAttributes.Public | TypeAttributes.SequentialLayout | TypeAttributes.Sealed,
            typeof(ValueType), pack == 0 ? PackingSize.Unspecified : (PackingSize)pack);

        foreach (var f in src.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var fb = tb.DefineField(f.Name, f.FieldType, FieldAttributes.Public);
            var ma = f.GetCustomAttribute<MarshalAsAttribute>();
            if (ma is not null)
            {
                var ctor = typeof(MarshalAsAttribute).GetConstructor(new[] { typeof(UnmanagedType) })!;
                var sizeConst = typeof(MarshalAsAttribute).GetField("SizeConst")!;
                fb.SetCustomAttribute(ma.Value == UnmanagedType.ByValTStr || ma.Value == UnmanagedType.ByValArray
                    ? new CustomAttributeBuilder(ctor, new object[] { ma.Value }, new[] { sizeConst }, new object[] { ma.SizeConst })
                    : new CustomAttributeBuilder(ctor, new object[] { ma.Value }));
            }
        }
        return tb.CreateType()!;
    }
}
