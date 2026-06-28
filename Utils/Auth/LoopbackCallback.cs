using System.Net;
using System.Text;

namespace MuxSwarm.Utils.Auth;

/// <summary>
/// One-shot localhost HTTP listener that catches the OAuth redirect. Binds 127.0.0.1 on the provider's
/// fixed redirect port + path, waits for the browser GET carrying ?code=&amp;state=, validates the state
/// (CSRF), renders a friendly success/failure page so the user can close the tab, and returns the code.
/// A loopback HttpListener prefix does NOT require admin rights on Windows.
/// </summary>
internal static class LoopbackCallback
{
    public sealed record Result(string Code, string? State, string? Error);

    /// <summary>
    /// Listen on http://127.0.0.1:{port}{path}/ for a single callback request and return the parsed result.
    /// Honors cancellation (stops the listener). Throws on listener-start failure (e.g. port in use).
    /// </summary>
    public static async Task<Result> WaitForAsync(int port, string path, CancellationToken ct)
    {
        string prefix = $"http://127.0.0.1:{port}{(path.EndsWith('/') ? path : path + "/")}";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        try
        {
            var ctxTask = listener.GetContextAsync();
            await using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                HttpListenerContext ctx;
                try { ctx = await ctxTask.ConfigureAwait(false); }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }

                var q = ctx.Request.QueryString;
                string? code = q["code"];
                string? state = q["state"];
                string? error = q["error"] ?? q["error_description"];

                string title = error is null && !string.IsNullOrEmpty(code) ? "Login complete" : "Login failed";
                string detail = error is null && !string.IsNullOrEmpty(code)
                    ? "You can close this tab and return to the terminal."
                    : WebUtility.HtmlEncode(error ?? "No authorization code was returned.");
                string html =
                    "<!doctype html><html><head><meta charset=\"utf-8\"><title>Mux-Swarm</title></head>" +
                    "<body style=\"font-family:system-ui;background:#0d1117;color:#c9d1d9;display:flex;" +
                    "align-items:center;justify-content:center;height:100vh;margin:0\">" +
                    $"<div style=\"text-align:center\"><h2 style=\"color:#82C49B\">{title}</h2><p>{detail}</p></div>" +
                    "</body></html>";
                byte[] buf = Encoding.UTF8.GetBytes(html);
                try
                {
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = buf.Length;
                    await ctx.Response.OutputStream.WriteAsync(buf, ct).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch { /* best-effort page render */ }

                return new Result(code ?? "", state, error);
            }
        }
        finally
        {
            if (listener.IsListening) { try { listener.Stop(); } catch { } }
        }
    }
}
