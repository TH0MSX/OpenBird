using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Tar;
using OpenBird.Core;

namespace OpenBird.Speech;

/// <summary>Locates and downloads the speech model, so nothing has to be placed by hand.</summary>
public static class WhisperModel
{
    public const string Encoder = "base.en-encoder.int8.onnx";
    public const string Decoder = "base.en-decoder.int8.onnx";
    public const string Tokens = "base.en-tokens.txt";
    public static readonly string[] RequiredFiles = { Encoder, Decoder, Tokens };

    public static readonly Uri DownloadUri = new(
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/{SettingsStore.WhisperModelName}.tar.bz2");

    public static IReadOnlyList<string> MissingFiles(string modelDir) =>
        RequiredFiles.Where(f => !File.Exists(Path.Combine(modelDir, f))).ToList();

    public static bool IsInstalled(string modelDir) => MissingFiles(modelDir).Count == 0;

    /// <summary>
    /// Downloads the model archive (about 200 MB) and keeps only the three files
    /// the recognizer needs. Progress is reported as (bytes done, bytes total).
    /// </summary>
    public static async Task InstallAsync(string modelDir, HttpClient http, IProgress<(long Done, long Total)>? progress = null,
        Uri? source = null, CancellationToken ct = default)
    {
        if (IsInstalled(modelDir)) return;
        Directory.CreateDirectory(modelDir);
        var archive = Path.Combine(modelDir, "download.tar.bz2.part");
        try
        {
            using (var response = await http.GetAsync(source ?? DownloadUri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(archive);
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report((done, total));
                }
                if (total > 0 && done != total) throw new IOException($"Download incomplete: {done} of {total} bytes.");
            }
            ExtractRequiredFiles(archive, modelDir);
            var missing = MissingFiles(modelDir);
            if (missing.Count > 0) throw new IOException($"The model archive did not contain: {string.Join(", ", missing)}");
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    public static void ExtractRequiredFiles(string archivePath, string modelDir)
    {
        using var file = File.OpenRead(archivePath);
        using var bz = new BZip2InputStream(file);
        using var tar = new TarInputStream(bz, System.Text.Encoding.UTF8);
        while (tar.GetNextEntry() is { } entry)
        {
            var name = Path.GetFileName(entry.Name);      // file name only: nothing can be written outside modelDir
            if (entry.IsDirectory || !RequiredFiles.Contains(name)) continue;
            var part = Path.Combine(modelDir, name + ".part");
            using (var output = File.Create(part)) tar.CopyEntryContents(output);
            File.Move(part, Path.Combine(modelDir, name), overwrite: true);
        }
    }
}
