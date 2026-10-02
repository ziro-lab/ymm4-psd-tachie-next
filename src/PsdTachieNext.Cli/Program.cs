using PsdTachieNext.Compiler;
using PsdTachieNext.Core;

try
{
    if (args is ["compile", var source, var cacheRoot])
    {
        var compiledDirectory = new PsdCompiler().Compile(source, cacheRoot);
        Console.WriteLine(compiledDirectory);
        return 0;
    }
    if (args is [var command, var directory] && command is "inspect" or "verify")
    {
        using var doc = new CompiledDocument(directory);
        var m = doc.Manifest;
        if (command == "verify") doc.VerifyAll();
        Console.WriteLine($"schema={m.SchemaVersion} generation={m.GenerationId}");
        Console.WriteLine($"canvas={m.Width}x{m.Height} nodes={m.Nodes.Length} blocks={m.Blocks.Length}");
        Console.WriteLine($"pixel_block_reads={doc.BlockReadCount} stored_bytes_read={doc.BytesRead}");
        foreach (var n in m.Nodes) Console.WriteLine($"{n.Id} parent={n.ParentId?.ToString() ?? "root"} order={n.Order} {n.Kind} {n.Name}");
        return 0;
    }
    Console.Error.WriteLine("Usage: compile <source.psd> <cache-directory> | inspect <compiled-directory> | verify <compiled-directory>");
    return 2;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
    return 1;
}
