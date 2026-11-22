using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CSharpNLVED.Core.Net;

/// <summary>One shared HttpClient for the whole app, plus small helpers.</summary>
public static class Http
{
    public static HttpClient Client { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        // No global timeout: big downloads can take a while. Each call sets its own.
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppInfo.Name, AppInfo.Version));
        return client;
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
    }

    public static async Task<string> GetStringAsync(Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return await Client.GetStringAsync(url, cts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Streams <paramref name="url"/> to <paramref name="destination"/>, reporting
    /// (bytes received, total bytes if known). If <paramref name="expectedSha256"/> is given,
    /// the file is verified and deleted on mismatch.
    /// </summary>
    public static async Task DownloadFileAsync(
        Uri url,
        string destination,
        string? expectedSha256,
        IProgress<(long Received, long? Total)>? progress,
        CancellationToken ct)
    {
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
                         bufferSize: 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                progress?.Report((received, total));
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actual = Convert.ToHexString(hash.GetHashAndReset());
            if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(destination);
                throw new InvalidDataException(
                    $"Checksum mismatch for {Path.GetFileName(url.LocalPath)}. The download may be corrupted.");
            }
        }
    }
}
