using System.Reflection;
using System.Text;

static class Dump
{
    public static void Run()
    {
        var asm = typeof(Microsoft.Dism.DismApi).Assembly;
        Console.WriteLine($"Assembly: {asm.FullName}");
        Console.WriteLine($"Location: {asm.Location}");
        Console.WriteLine();

        var api = typeof(Microsoft.Dism.DismApi);
        var methods = api.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name).ThenBy(m => m.GetParameters().Length);

        Console.WriteLine("=== DismApi public static methods ===");
        string? last = null;
        foreach (var m in methods)
        {
            if (m.Name != last) { Console.WriteLine(); last = m.Name; }
            Console.WriteLine("  " + Sig(m));
        }

        Console.WriteLine();
        Console.WriteLine("=== All public types in Microsoft.Dism ===");
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.Name))
            Console.WriteLine($"  {(t.IsEnum ? "enum  " : t.IsInterface ? "iface " : t.IsValueType ? "struct" : "class ")} {t.FullName}");
    }

    static string Sig(MethodInfo m)
    {
        var sb = new StringBuilder();
        sb.Append(Pretty(m.ReturnType)).Append(' ').Append(m.Name).Append('(');
        sb.Append(string.Join(", ", m.GetParameters().Select(p =>
            $"{(p.IsOut ? "out " : p.ParameterType.IsByRef ? "ref " : "")}{Pretty(p.ParameterType)} {p.Name}{(p.HasDefaultValue ? " = " + (p.DefaultValue ?? "null") : "")}")));
        sb.Append(')');
        return sb.ToString();
    }

    static string Pretty(Type t)
    {
        if (t.IsByRef) t = t.GetElementType()!;
        if (t == typeof(void)) return "void";
        if (t == typeof(string)) return "string";
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(bool)) return "bool";
        if (t.IsGenericType)
            return t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Pretty)) + ">";
        return t.Name;
    }
}
