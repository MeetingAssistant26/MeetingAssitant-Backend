using System;
using System.IO;
using System.Linq;
using System.Reflection;

class FindSchema
{
    static void Main()
    {
        string packagesPath = @"C:\Users\DEll\.nuget\packages\";
        if (!Directory.Exists(packagesPath))
        {
            Console.WriteLine("Packages path not found.");
            return;
        }

        Console.WriteLine($"Searching for IOpenApiSchema in {packagesPath}...");

        var dllFiles = Directory.EnumerateFiles(packagesPath, "Microsoft.OpenApi*.dll", SearchOption.AllDirectories)
                                .Concat(Directory.EnumerateFiles(packagesPath, "Swashbuckle*.dll", SearchOption.AllDirectories));

        foreach (var dll in dllFiles)
        {
            try
            {
                var asm = Assembly.LoadFile(dll);
                foreach (var t in asm.GetTypes())
                {
                    if (t.Name.Contains("IOpenApiSchema"))
                    {
                        Console.WriteLine($"Found {t.FullName} in {dll}");
                    }
                }
            }
            catch
            {
                // Ignore load exceptions for dependencies
            }
        }
        Console.WriteLine("Done.");
    }
}