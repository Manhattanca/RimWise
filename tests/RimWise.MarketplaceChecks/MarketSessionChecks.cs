using System.Net;
using RimWise.Integration.AI;
using RimWise.Integration.Marketplace;

internal static class MarketSessionChecks
{
    private static readonly Uri Origin = new("https://market.example:5443/");
    private sealed class Secrets : ISecretStore
    {
        internal readonly Dictionary<string, string> Values = [];
        public string? Read(string name) => Values.GetValueOrDefault(name);
        public void Write(string name, string value) => Values[name] = value;
        public void Delete(string name) => Values.Remove(name);
    }
    private sealed class Transport(CookieContainer cookies) : HttpMessageHandler
    {
        internal bool SignIn, Fail, Reject;
        internal TaskCompletionSource? Wait;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Seen;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Seen = cookies.GetCookies(Origin)[MarketSessionHandler.CookieName]?.Value;
            Started.TrySetResult();
            if (Wait is not null) await Wait.Task.WaitAsync(token);
            if (Fail) throw new HttpRequestException("Synthetic offline");
            var response = new HttpResponseMessage(Reject ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
            if (SignIn)
            {
                cookies.Add(Origin, new Cookie(MarketSessionHandler.CookieName, new string('a', 64), "/"));
                response.Headers.TryAddWithoutValidation("Set-Cookie", MarketSessionHandler.CookieName + "=" + new string('a', 64) + "; Path=/; Secure; HttpOnly");
            }
            return response;
        }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Session isolation or persistence regression"); }
    internal static IEnumerable<(string Name, Func<Task> Check)> All()
    {
        yield return ("native HTTP cookie transport restores a session in a replacement client", async () =>
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            using var stop = new CancellationTokenSource();
            var server = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var socket = await listener.AcceptTcpClientAsync(stop.Token);
                        using var stream = socket.GetStream();
                        using var reader = new StreamReader(stream, leaveOpen: true);
                        string first = await reader.ReadLineAsync(stop.Token) ?? "";
                        bool authenticated = false;
                        while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 } line)
                            authenticated |= line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)
                                && line.Contains(MarketSessionHandler.CookieName + "=" + new string('b', 64));
                        string body = authenticated ? "signed-in" : "anonymous";
                        string cookie = first.Contains("/api/login ") ? "Set-Cookie: " + MarketSessionHandler.CookieName + "=" + new string('b', 64) + "; Path=/; HttpOnly\r\n" : "";
                        var bytes = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n" + cookie
                            + "Content-Length: " + body.Length + "\r\n\r\n" + body);
                        await stream.WriteAsync(bytes, stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            try
            {
                var secrets = new Secrets();
                using (var first = new HttpClient(new MarketSessionHandler(origin, "native-fixture", secrets)))
                    using (await first.GetAsync(new Uri(origin, "api/login"))) { }
                Check(secrets.Values.Count == 1);
                using var next = new HttpClient(new MarketSessionHandler(origin, "native-fixture", secrets));
                Check(await next.GetStringAsync(new Uri(origin, "api/me")) == "signed-in");
                using var separate = new HttpClient(new MarketSessionHandler(origin, "review-fixture", secrets));
                Check(await separate.GetStringAsync(new Uri(origin, "api/me")) == "anonymous");
            }
            finally { stop.Cancel(); listener.Stop(); await server; }
        });
        yield return ("session survives client replacement without storing password or CSRF", async () =>
        {
            var secrets = new Secrets(); var cookies = new CookieContainer();
            using (var client = new HttpClient(new MarketSessionHandler(Origin, "desktop", secrets, new Transport(cookies) { SignIn = true }, cookies)))
                using (await client.GetAsync(new Uri(Origin, "api/login"))) { }
            Check(secrets.Values.Count == 1 && !secrets.Values.Single().Value.Contains("password") && !secrets.Values.Single().Value.Contains("csrf"));
            var restored = new CookieContainer(); var transport = new Transport(restored);
            using var next = new HttpClient(new MarketSessionHandler(Origin, "desktop", secrets, transport, restored));
            using var response = await next.GetAsync(new Uri(Origin, "api/me"));
            Check(transport.Seen == new string('a', 64));
        });
        yield return ("session is isolated by origin and desktop versus review scope", async () =>
        {
            var secrets = new Secrets(); var cookies = new CookieContainer();
            using (var client = new HttpClient(new MarketSessionHandler(Origin, "desktop", secrets, new Transport(cookies) { SignIn = true }, cookies)))
                using (await client.GetAsync(new Uri(Origin, "api/login"))) { }
            foreach (var (origin, scope) in new[] { (Origin, "review"), (new Uri("https://other.example:5443/"), "desktop"), (new Uri("https://market.example:5444/"), "desktop") })
            {
                var isolated = new CookieContainer();
                using var handler = new MarketSessionHandler(origin, scope, secrets, new Transport(isolated), isolated);
                Check(isolated.GetCookies(origin)[MarketSessionHandler.CookieName] is null);
            }
            var jar = new CookieContainer(); using var guard = new HttpClient(new MarketSessionHandler(Origin, "desktop", secrets, new Transport(jar), jar));
            bool denied = false;
            try { using var ignored = await guard.GetAsync("https://foreign.example/api/me"); } catch (InvalidOperationException) { denied = true; }
            Check(denied);
        });
        yield return ("offline requests do not erase login and protected 401 does", async () =>
        {
            var secrets = new Secrets(); var cookies = new CookieContainer(); var transport = new Transport(cookies) { SignIn = true };
            using var handler = new MarketSessionHandler(Origin, "desktop", secrets, transport, cookies);
            using var client = new HttpClient(handler);
            using (await client.GetAsync(new Uri(Origin, "api/login"))) { }
            transport.SignIn = false; transport.Fail = true;
            try { using var ignored = await client.GetAsync(new Uri(Origin, "api/me")); } catch (HttpRequestException) { }
            Check(secrets.Values.Count == 1);
            transport.Fail = false; transport.Reject = true;
            using (await client.GetAsync(new Uri(Origin, "api/password"))) { }
            Check(secrets.Values.Count == 1);
            using (await client.GetAsync(new Uri(Origin, "api/mine"))) { }
            Check(secrets.Values.Count == 0 && cookies.GetCookies(Origin)[MarketSessionHandler.CookieName] is null);
        });
        yield return ("logout clearing cannot be undone by an earlier in-flight login response", async () =>
        {
            var secrets = new Secrets(); var cookies = new CookieContainer();
            var transport = new Transport(cookies) { SignIn = true, Wait = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            using var handler = new MarketSessionHandler(Origin, "desktop", secrets, transport, cookies);
            using var client = new HttpClient(handler);
            var pending = client.GetAsync(new Uri(Origin, "api/login"));
            await transport.Started.Task; handler.Clear(); transport.Wait.SetResult();
            using var response = await pending;
            Check(secrets.Values.Count == 0 && cookies.GetCookies(Origin)[MarketSessionHandler.CookieName] is null);
        });
        yield return ("corrupt and expired saved sessions are not restored", async () =>
        {
            var secrets = new Secrets(); var cookies = new CookieContainer();
            using (var client = new HttpClient(new MarketSessionHandler(Origin, "desktop", secrets, new Transport(cookies) { SignIn = true }, cookies)))
                using (await client.GetAsync(new Uri(Origin, "api/login"))) { }
            var key = secrets.Values.Keys.Single();
            foreach (var value in new[] { "broken-json", "{\"Origin\":\"https://market.example:5443/\",\"Token\":\"" + new string('a',64) + "\",\"Expires\":\"2020-01-01T00:00:00Z\"}" })
            {
                secrets.Values[key] = value; var restored = new CookieContainer();
                using var handler = new MarketSessionHandler(Origin, "desktop", secrets, new Transport(restored), restored);
                Check(restored.GetCookies(Origin)[MarketSessionHandler.CookieName] is null);
            }
        });
    }
}
