using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HoneAI;

/// <summary>Appending and reading one-JSON-object-per-line files — what the JSONL sink and stores share.</summary>
internal static class JsonlFile
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Appends <paramref name="value"/> as one line in a single write, so a line another process
    /// appends at the same time is not interleaved into it. A file whose last line a crash cut short
    /// gets a line break first — otherwise this line would be glued to the torn one and lost with it.
    /// </summary>
    public static async Task AppendAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var line = JsonSerializer.Serialize(value, Options) + "\n";
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
                line = "\n" + line;
        }
        stream.Seek(0, SeekOrigin.End);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The file's lines as <typeparamref name="T"/>, in order; a missing file is empty, a torn last line is skipped.</summary>
    public static async IAsyncEnumerable<T> ReadAsync<T>(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            yield break;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(line, Options);
            }
            catch (JsonException)
            {
                // A line cut short by a crash mid-append: the event it carried did not happen.
                continue;
            }
            if (value is not null)
                yield return value;
        }
    }
}
