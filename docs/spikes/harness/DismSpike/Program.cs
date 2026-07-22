var mode = args.Length > 0 ? args[0] : "dump";

switch (mode)
{
    case "dump": Dump.Run(); break;
    case "pinvoke": NativeAppx.Run(args.Skip(1).ToArray()); break;
    case "managed": ManagedProbe.Run(args.Skip(1).ToArray()); break;
    case "bench": Bench.Run(args.Skip(1).ToArray()); break;
    case "layout": Layout.Run(); break;
    case "winmd": Winmd.Run(args.Skip(1).ToArray()); break;
    default: Console.Error.WriteLine($"unknown mode '{mode}'"); return 2;
}
return 0;
