namespace StoreZip;

/// <summary>
/// The result of converting one zip: where it went, or why it couldn't be.
/// <paramref name="Unchanged"/>: converted in place, but it already was a StoreZip, so the file wasn't touched.
/// </summary>
public sealed record StoreZipJobResult(string Source, string Output, StoreZipResult? Result, string? Error, bool Unchanged = false);

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
            return new StoreZipJobResult(source, output, null, Why(ex));
        }
    }

    /// <summary>
    /// Replace a zip with its StoreZip, keeping its name. The StoreZip is written
    /// beside it and read back (its SHA-1 must match what was written) before the
    /// original is replaced; any error leaves the original as it was. A zip that
    /// already is a StoreZip isn't touched.
    /// </summary>
    public static StoreZipJobResult ConvertInPlace(string source, CancellationToken cancel)
    {
        string temp = source + ".storezip.tmp";
        try
        {
            StoreZipResult result;
            using (var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                result = StoreZipWriter.Write(src, dst, cancel);

            if (Sha1Of(temp, cancel) != result.Sha1)
                return new StoreZipJobResult(source, source, null, "the StoreZip read back differently from what was written (disk problem?) — the original was kept");
            if (new FileInfo(source).Length == result.Size && Sha1Of(source, cancel) == result.Sha1)
                return new StoreZipJobResult(source, source, result, null, Unchanged: true);
            File.Move(temp, source, overwrite: true);
            return new StoreZipJobResult(source, source, result, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StoreZipJobResult(source, source, null, Why(ex) + " — the original was kept");
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private static string Sha1Of(string path, CancellationToken cancel)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        using var sha1 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = fs.Read(buffer)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            sha1.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(sha1.GetHashAndReset());
    }

    private static string Why(Exception ex) => ex is InvalidDataException ? "not a readable zip (" + ex.Message + ")" : ex.Message;
}
