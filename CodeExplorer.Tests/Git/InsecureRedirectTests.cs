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
///     is self-signed, and the server rightly offers no way to accept one. The callback below is the
///     only option here that <c>GitClones</c> does not set; none of the ones it sets bear on redirects.
///     The ref advertisement <c>GitClones</c> also reads goes through the same libgit2 transport, but
///     <c>ListRemoteReferences</c> takes no certificate callback, so it cannot reach this remote at all.
/// </summary>
public sealed class InsecureRedirectTests : IDisposable
{
    private const string Refusal = "cannot redirect from 'https' to 'http'";

    /// <summary>One for the class: generating an RSA key is the slowest thing either test does.</summary>
    private static readonly X509Certificate2 _certificate = SelfSigned();

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "CodeExplorer.Tests", Guid.NewGuid().ToString("N"));

    private readonly TcpListener _https = new(IPAddress.Loopback, 0);
    private readonly TcpListener _http = new(IPAddress.Loopback, 0);
    private int _httpConnections;

    public InsecureRedirectTests()
    {
        _https.Start();
        _http.Start();
        Accept(_https, Redirect);
        // Any connection at all to the http side is a followed redirect; it is counted and hung up on.
        Accept(_http, socket =>
        {
            Interlocked.Increment(ref _httpConnections);
            socket.Dispose();
        });
    }

    private string RemoteUrl => $"https://127.0.0.1:{((IPEndPoint)_https.LocalEndpoint).Port}/remote.git";

    public void Dispose()
    {
        _https.Dispose();
        _http.Dispose();
        TestHost.DeleteTree(_root);
    }

    [Fact]
    public void A_clone_does_not_follow_an_https_remote_to_http()
    {
        var options = new CloneOptions { IsBare = true };
        options.FetchOptions.CertificateCheck = AcceptTestCertificate;

        var refused = Assert.ThrowsAny<LibGit2SharpException>(() =>
            Repository.Clone(RemoteUrl, Path.Combine(_root, "clone"), options));

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
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

        Assert.Contains(Refusal, refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, Volatile.Read(ref _httpConnections));
    }

    /// <summary>Accepts this test's own certificate and nothing else.</summary>
    private static bool AcceptTestCertificate(Certificate certificate, bool valid, string host) =>
        certificate is CertificateX509 { Certificate: { } presented }
        && presented.GetCertHashString() == _certificate.GetCertHashString();

    /// <summary>
    ///     Answers a request with a redirect to the same path on the http listener, which is what a
    ///     remote moved to a plain-http host, or an attacker able to answer for the https one, sends.
    /// </summary>
    private void Redirect(Socket socket)
    {
        try
        {
            using var tls = new SslStream(new NetworkStream(socket, ownsSocket: true));
            tls.AuthenticateAsServer(_certificate);
            // The request line names the path; the headers after it, up to the blank line, are not needed.
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            string target = reader.ReadLine()?.Split(' ')[1] ?? "/";
            while (!string.IsNullOrEmpty(reader.ReadLine())) { }

            int port = ((IPEndPoint)_http.LocalEndpoint).Port;
            tls.Write(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}{target}\r\n"
                + "Content-Length: 0\r\nConnection: close\r\n\r\n"));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException
                                       or AuthenticationException)
        {
            // Safe to swallow: the client hung up or the test ended, and the assertions are on the clone.
        }
    }

    /// <summary>
    ///     Hands every connection to <paramref name="handle" /> on a thread of its own, as
    ///     <c>StalledRemoteTests</c> does and for its reason: libgit2 blocks the caller, so a handler
    ///     waiting on the thread pool could wait behind the very test it answers.
    /// </summary>
    private static void Accept(TcpListener listener, Action<Socket> handle) => StartThread(() =>
    {
        try
        {
            while (true)
            {
                var socket = listener.AcceptSocket();
                StartThread(() => handle(socket));
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
