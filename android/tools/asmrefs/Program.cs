// asmrefs <файл.dll|exe>… — по строке на файл: «файл<TAB>имя1,имя2…» (ссылки AssemblyRef)
using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

foreach (var path in args)
{
    try
    {
        using var pe = new PEReader(File.OpenRead(path));
        if (!pe.HasMetadata) { Console.WriteLine(path + "\t"); continue; }
        var md = pe.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name));
        Console.WriteLine(path + "\t" + string.Join(",", refs));
    }
    catch (BadImageFormatException)
    {
        Console.WriteLine(path + "\t");
    }
}
