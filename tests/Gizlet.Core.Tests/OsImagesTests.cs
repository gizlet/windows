using System.Net;
using System.Text;
using Gizlet.Core.Flash;
using Gizlet.Core.Signing;

namespace Gizlet.Core.Tests;

/// <summary>Official images come only from the Gizlet server the API runs on.</summary>
public class OsImagesTests
{
    sealed class Server(Func<Uri, HttpResponseMessage?> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
}
