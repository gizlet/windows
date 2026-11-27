using System.Net;
using System.Text;
using Gizlet.Core.Flash;
using Gizlet.Core.Signing;

namespace Gizlet.Core.Tests;

/// <summary>Official images come only from the Gizlet server the API runs on.</summary>
public class OsImagesTests
{
    sealed class Server(Func<Uri, HttpResponseMessage?> answer, bool refuseRanges = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (refuseRanges && request.Headers.Range is not null) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { RequestMessage = request });
            var response = answer(request.RequestUri!) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }

    static HttpResponseMessage Ok(byte[] body, Uri? servedFrom = null) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
        RequestMessage = servedFrom is null ? null : new HttpRequestMessage(HttpMethod.Get, servedFrom),
    };

    static OsImages Images(string manifestHost, Uri? redirectManifestTo = null)
    {
        var api = new Uri("https://gizlet.io/api/");
        var latest = $$"""{"tag":"v0.4.4","manifest_url":"https://{{manifestHost}}/assets/releases/v0.4.4/manifest.json","signature_url":"https://{{manifestHost}}/assets/releases/v0.4.4/manifest.json.sig"}""";
        var server = new Server(uri => uri.AbsolutePath switch
        {
            "/api/v1/os/releases/latest" => Ok(Encoding.UTF8.GetBytes(latest)),
            "/assets/releases/v0.4.4/manifest.json" => Ok(Fixtures.Bytes("os-v0.4.4-manifest.json"), redirectManifestTo),
            "/assets/releases/v0.4.4/manifest.json.sig" => Ok(Fixtures.Bytes("os-v0.4.4-manifest.json.sig")),
            _ => null,
        });
        return new OsImages(new HttpClient(server), SshPublicKey.Parse(OsImages.ReleaseKey), Path.GetTempPath(), api);
    }

    [Fact]
    public async Task FindsTheSignedLatestRelease()
    {
        var release = await Images("gizlet.io").LatestAsync();
        Assert.Equal("v0.4.4", release.Tag);
        Assert.Equal("https://gizlet.io/assets/releases/v0.4.4/boards/gameconsole/r36s/gizlet-os-r36s.img.gz", release.Image.ToString());
    }

    [Fact]
    public async Task RefusesASignedManifestFromAnotherHost()
    {
        var error = await Assert.ThrowsAsync<GizletException>(() => Images("evil.example").LatestAsync());
        Assert.Equal(GizletError.SignatureInvalid, error.Error);
    }

    [Fact]
    public async Task RefusesARedirectAwayFromTheServer()
    {
        var error = await Assert.ThrowsAsync<GizletException>(() =>
            Images("gizlet.io", new Uri("https://evil.example/assets/releases/v0.4.4/manifest.json")).LatestAsync());
        Assert.Equal(GizletError.SignatureInvalid, error.Error);
    }

    /// <summary>A release that signs its image's digest, served by a stub Gizlet server.</summary>
    static OsImages Signed(string directory, string sums, byte[]? image = null, bool refuseRanges = false)
    {
        var server = new Server(uri => uri.AbsolutePath switch
        {
            "/api/v1/os/releases/latest" => Ok(Encoding.UTF8.GetBytes("""{"tag":"v9.9.9","manifest_url":"https://gizlet.io/assets/releases/v9.9.9/manifest.json","signature_url":"https://gizlet.io/assets/releases/v9.9.9/manifest.json.sig"}""")),
            "/assets/releases/v9.9.9/manifest.json" => Ok(Fixtures.Bytes("os-v9.9.9-manifest.json")),
            "/assets/releases/v9.9.9/manifest.json.sig" => Ok(Fixtures.Bytes("os-v9.9.9-manifest.json.sig")),
            "/assets/releases/v9.9.9/boards/gameconsole/r36s/SHA256SUMS" => Ok(Encoding.ASCII.GetBytes(sums)),
            "/assets/releases/v9.9.9/boards/gameconsole/r36s/gizlet-os-r36s.img.gz" => Ok(image ?? Fixtures.Bytes("os-v9.9.9-image.img.gz")),
            _ => null,
        }, refuseRanges);
        return new OsImages(new HttpClient(server), SshPublicKey.Parse(Fixtures.Text("test-os-key.pub")), directory, new Uri("https://gizlet.io/api/"));
    }

    static string Sums(byte[] image) => $"{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image))}  gizlet-os-r36s.img.gz\n";

    [Fact]
    public async Task DownloadsAnImageMatchingItsSignedDigest()
    {
        using var temp = new TempDirectory();
        var images = Signed(temp.Path, Sums(Fixtures.Bytes("os-v9.9.9-image.img.gz")));
        var release = await images.LatestAsync();
        Assert.Equal(Fixtures.Bytes("os-v9.9.9-image.img.gz").Length, release.ImageBytes);
        var image = await images.DownloadAsync(release);
        Assert.True(image.Official);
        Assert.Equal(16 * 4096, image.Size);
    }

    [Fact]
    public async Task RefusesChecksumsThatContradictTheSignedDigest()
    {
        using var temp = new TempDirectory();
        var forged = Fixtures.Bytes("os-v9.9.9-image.img.gz").ToArray();
        forged[^5] ^= 1;
        var images = Signed(temp.Path, Sums(forged), forged);
        var release = await images.LatestAsync();
        var error = await Assert.ThrowsAsync<GizletException>(() => images.DownloadAsync(release));
        Assert.Equal(GizletError.ImageBadChecksum, error.Error);

        // The right SHA256SUMS does not let another image through either.
        using var again = new TempDirectory();
        images = Signed(again.Path, Sums(Fixtures.Bytes("os-v9.9.9-image.img.gz")), forged);
        error = await Assert.ThrowsAsync<GizletException>(() => images.DownloadAsync(release));
        Assert.Equal(GizletError.ImageBadChecksum, error.Error);
    }

    [Fact]
    public async Task StartsAgainWhenAPartialDownloadIsAlreadyComplete()
    {
        using var temp = new TempDirectory();
        var image = Fixtures.Bytes("os-v9.9.9-image.img.gz");
        var images = Signed(temp.Path, Sums(image), refuseRanges: true);
        var release = await images.LatestAsync();
        // Cut short after the last byte, before the rename: asking for the rest answers 416.
        Directory.CreateDirectory(Path.Combine(temp.Path, "v9.9.9"));
        File.WriteAllBytes(Path.Combine(temp.Path, "v9.9.9", "gizlet-os-r36s.img.gz.partial"), image);
        var card = await images.DownloadAsync(release);
        Assert.Equal(16 * 4096, card.Size);
    }
}
