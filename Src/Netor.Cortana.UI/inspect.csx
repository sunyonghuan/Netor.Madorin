using System;
using System.Reflection;
using System.Linq;

var asm = Assembly.LoadFrom(@"E:\软件工程\Netor.me\Netor.Nuget\Packages\avalonia\12.0.0\lib\net10.0\Avalonia.Base.dll");
var dragType = asm.GetType("Avalonia.Input.DragEventArgs");
if (dragType != null) {
    Console.WriteLine("=== DragEventArgs Properties ===");
    foreach (var p in dragType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
        Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
    }
    Console.WriteLine("=== Interfaces ===");
    foreach (var i in dragType.GetInterfaces()) {
        Console.WriteLine($"  {i.Name}");
    }
} else { Console.WriteLine("DragEventArgs not found"); }
