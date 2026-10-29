namespace StoreZip;

/// <summary>The result of converting one zip: where it went, or why it couldn't be.</summary>
public sealed record StoreZipJobResult(string Source, string Output, StoreZipResult? Result, string? Error);

/// <summary>What the StoreZip app (and the CLI) do with the files they're given.</summary>
public static class StoreZipJob
{
    /// <summary>Zips given directly, plus every .zip / .zip_ in folders given (searched recursively), in name order.</summary>
    public static List<string> Expand(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (string input in inputs)
        {
            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories).Where(IsZip)
                                        .Order(StringComparer.OrdinalIgnoreCase));
            }
            else if (File.Exists(input))
            {
                files.Add(input);
            }
        }
        return files.Distinct(StringComparer.Ordinal).ToList();
    }

    public static bool IsZip(string path) =>
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".zip_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The output file for a source zip: same name, .zip_ when <paramref name="renameExtension"/>.</summary>
    public static string OutputPath(string source, string outputFolder, bool renameExtension)
    {
        string name = Path.GetFileName(source);
        string stem = name.EndsWith(".zip_", StringComparison.OrdinalIgnoreCase) ? name[..^5]
                    : name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4]
                    : name;
        return Path.Combine(outputFolder, stem + (renameExtension ? ".zip_" : ".zip"));
    }

    /// <summary>Convert one zip into the output folder (an existing file of the same name is replaced).</summary>
    public static StoreZipJobResult ConvertOne(string source, string outputFolder, bool renameExtension, CancellationToken cancel)
    {
        string output = OutputPath(source, outputFolder, renameExtension);
        try
        {
            Directory.CreateDirectory(outputFolder);
            var result = StoreZipWriter.Convert(source, output, cancel);
            return new StoreZipJobResult(source, output, result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string why = ex is InvalidDataException ? "not a readable zip (" + ex.Message + ")" : ex.Message;
            return new StoreZipJobResult(source, output, null, why);
        }
    }
}
