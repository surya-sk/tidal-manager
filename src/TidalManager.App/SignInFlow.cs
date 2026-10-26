using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TidalManager.App;

// Browser sign-in with PKCE. A loopback redirect returns automatically; otherwise the user pastes
// the final URL from the browser.
internal sealed class SignInFlow
{
    private TaskCompletionSource<Uri>? _manualCallback;

    public bool IsWaiting => _manualCallback is { Task.IsCompleted: false };

    public async Task SignInAsync(string clientId, Uri redirectUri, Action<string> status, CancellationToken ct)
    {
        var authorization = App.Auth.CreateAuthorization(clientId, redirectUri);
        await App.SaveAccountSettingsAsync(authorization.ClientId, redirectUri.ToString());
        _manualCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listenerCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<Uri>? loopback = null;
        if (redirectUri.Scheme == "http" && redirectUri.Host is "127.0.0.1" or "localhost")
        {
            try { loopback = ListenAsync(StartListener(redirectUri), redirectUri, listenerCancellation.Token); }
            catch (SocketException ex)
            {
                status($"Port {redirectUri.Port} is busy ({ex.Message}). After signing in, paste the browser's address below.");
            }
        }
        if (loopback is not null) status("Finish signing in in your browser.");
        Process.Start(new ProcessStartInfo(authorization.AuthorizationUri.ToString()) { UseShellExecute = true });
        var manual = _manualCallback.Task.WaitAsync(ct);
        var callback = loopback is null ? await manual : await await Task.WhenAny(loopback, manual);
        await listenerCancellation.CancelAsync();
        await App.Auth.CompleteAuthorizationAsync(authorization, callback, ct);
    }

    public void CompleteManually(Uri callback) => _manualCallback?.TrySetResult(callback);

    private static TcpListener StartListener(Uri redirectUri)
    {
        var listener = new TcpListener(IPAddress.Loopback, redirectUri.Port);
        listener.Start();
        return listener;
    }

    private static async Task<Uri> ListenAsync(TcpListener listener, Uri redirectUri, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(ct);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(ct);
                if (requestLine is null || requestLine.Length > 4096) continue;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET") continue;
                var callback = new Uri(redirectUri, parts[1]);
                if (callback.AbsolutePath.TrimEnd('/') != redirectUri.AbsolutePath.TrimEnd('/'))
                {
                    // Browsers also ask for /favicon.ico and similar; only the redirect path is the callback.
                    await stream.WriteAsync("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                    continue;
                }
                var body = Encoding.UTF8.GetBytes(
                    "<html><body style=\"background:#1d1d1d;color:#fff;font-family:'Segoe UI Light',sans-serif;padding:60px 120px\">" +
                    "<h1 style=\"font-weight:200\">you're signed in</h1><p>You can return to tidal manager.</p></body></html>");
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, ct);
                await stream.WriteAsync(body, ct);
                return callback;
            }
        }
        finally { listener.Stop(); }
    }
}
