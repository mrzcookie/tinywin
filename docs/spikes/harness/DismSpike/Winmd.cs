using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// Reads Microsoft's own Win32 metadata (Windows.Win32.winmd, machine-generated from the
/// real SDK/ADK headers) to recover the authoritative packing and field order of the DISM
/// structs — including whether dismapi.h applies a #pragma pack.
/// </summary>
static class Winmd
{
    public static void Run(string[] args)
    {
        string path = args.FirstOrDefault() ?? throw new ArgumentException("need winmd path");
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();

        Console.WriteLine($"=== DISM types in {Path.GetFileName(path)} ===\n");

        foreach (var h in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(h);
            string name = md.GetString(td.Name);
            string ns = md.GetString(td.Namespace);
            if (!name.StartsWith("Dism", StringComparison.Ordinal)) continue;

            var layout = td.GetLayout();
            bool isEnum = false;
            if (!td.BaseType.IsNil && td.BaseType.Kind == HandleKind.TypeReference)
                isEnum = md.GetString(md.GetTypeReference((TypeReferenceHandle)td.BaseType).Name) == "Enum";

            if (isEnum) continue;

            Console.WriteLine($"{ns}.{name}   [PackingSize={(layout.IsDefault ? "default(none)" : layout.PackingSize.ToString())}, " +
                              $"ClassSize={(layout.IsDefault ? 0 : layout.Size)}]");

            foreach (var fh in td.GetFields())
            {
                var fd = md.GetFieldDefinition(fh);
                var sig = fd.DecodeSignature(new Sig(), null);
                Console.WriteLine($"    {sig,-28} {md.GetString(fd.Name)}");
            }
            Console.WriteLine();
        }
    }

    class Sig : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetPointerType(string e) => e + "*";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetArrayType(string e, ArrayShape s) => e + "[]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g;
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte rawKind) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte rawKind) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object? g, TypeSpecificationHandle h, byte rawKind) => "spec";
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericMethodParameter(object? g, int i) => "!!" + i;
        public string GetGenericTypeParameter(object? g, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool isRequired) => u;
        public string GetPinnedType(string e) => e;
    }
}
