using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using LibGit2Sharp;
using Xunit;

namespace CodeExplorer.Tests;

/// <summary>
///     An https remote that redirects to plain http is not followed (#299). A credential is only ever
///     stored beside an https or ssh URL (GHSA-4f8q-c6jj-fr44), so following the downgrade would send
///     it, and the code, over clear text after all.
///     libgit2 refuses the downgrade itself, and the server sets nothing that changes its redirect
///     policy, so these tests pin libgit2's own behaviour: an upgrade that follows the redirect fails
///     here. They call libgit2 directly rather than through a refresh because the remote's certificate
///     is self-signed, and the server rightly offers no way to accept one; the callback below is the
///     only difference from the options <c>GitClones</c> passes.
/// </summary>
public sealed class InsecureRedirectTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    private readonly TcpListener _https = new(IPAddress.Loopback, 0);
    private readonly TcpListener _http = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _certificate = SelfSigned();
    private int _httpConnections;

    public InsecureRedirectTests()
    {
        _https.Start();
        _http.Start();
        ServeRedirects();
        CountConnections();
    }

    private string RemoteUrl => $"https://127.0.0.1:{((IPEndPoint)_https.LocalEndpoint).Port}/remote.git";

    public void Dispose()
    {
        _https.Dispose();
        _http.Dispose();
        _certificate.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_clone_does_not_follow_an_https_remote_to_http()
    {
        var options = new CloneOptions { IsBare = true };
        options.FetchOptions.CertificateCheck = AcceptTestCertificate;

        var refused = Assert.ThrowsAny<LibGit2SharpException>(() =>
            Repository.Clone(RemoteUrl, Path.Combine(_root, "clone"), options));

        Assert.Contains("cannot redirect from 'https' to 'http'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _httpConnections));
    }

    [Fact]
    public void A_fetch_does_not_follow_an_https_remote_to_http()
    {
        string path = Repository.Init(Path.Combine(_root, "copy"), isBare: true);
        using var copy = new Repository(path);
        copy.Network.Remotes.Add("origin", RemoteUrl);

        var refused = Assert.ThrowsAny<LibGit2SharpException>(() => Commands.Fetch(copy, "origin",
            ["+refs/heads/*:refs/heads/*"], new FetchOptions { CertificateCheck = AcceptTestCertificate }, null));

        Assert.Contains("cannot redirect from 'https' to 'http'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _httpConnections));
    }

    /// <summary>Accepts this test's own certificate and nothing else.</summary>
    private bool AcceptTestCertificate(Certificate certificate, bool valid, string host) =>
        certificate is CertificateX509 { Certificate: { } presented }
        && presented.GetCertHashString() == _certificate.GetCertHashString();

    /// <summary>
    ///     Answers every request with a redirect to the same path on the http listener, which is what a
    ///     remote moved to a plain-http host, or an attacker able to answer for the https one, sends.
    /// </summary>
    private void ServeRedirects() => StartThread(() =>
    {
        try
        {
            while (true)
            {
                var client = _https.AcceptTcpClient();
                StartThread(() => Redirect(client));
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Safe to swallow: Dispose stops the listener, which is what ends this loop.
        }
    });

    private void Redirect(TcpClient client)
    {
        try
        {
            using (client)
            using (var tls = new SslStream(client.GetStream()))
            {
                tls.AuthenticateAsServer(_certificate);
                var buffer = new byte[4096];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    int read = tls.Read(buffer);
                    if (read == 0) return;
                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                string target = request.ToString().Split(' ')[1];
                int port = ((IPEndPoint)_http.LocalEndpoint).Port;
                tls.Write(Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}{target}\r\n"
                    + "Content-Length: 0\r\nConnection: close\r\n\r\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException
                                       or AuthenticationException)
        {
            // Safe to swallow: the client hung up or the test ended, and the assertions are on the clone.
        }
    }

    /// <summary>Counts every connection to the http side and hangs up; any at all is a followed redirect.</summary>
    private void CountConnections() => StartThread(() =>
    {
        try
        {
            while (true)
            {
                using var socket = _http.AcceptSocket();
                Interlocked.Increment(ref _httpConnections);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Safe to swallow: Dispose stops the listener, which is what ends this loop.
        }
    });

    private static void StartThread(Action body) => new Thread(new ThreadStart(body)) { IsBackground = true }.Start();

    /// <summary>
    ///     A throwaway certificate for 127.0.0.1. Round-tripped through PKCS#12 because SChannel will not
    ///     serve TLS with the ephemeral key <see cref="CertificateRequest" /> creates.
    /// </summary>
    private static X509Certificate2 SelfSigned()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
