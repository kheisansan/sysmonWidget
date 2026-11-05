using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysmonWidget
{
    sealed class GmailMailItem
    {
        public string From = "";
        public string Subject = "";
    }

    /// <summary>
    /// Gmail API (readonly) + デスクトップ OAuth (PKCE)。
    /// Client ID / Secret とトークンは Credential Manager（DPAPI 保護）に保存する。
    /// </summary>
    sealed class GmailClient
    {
        const string CredTarget = "SysmonWidget:GmailOAuth";
        const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
        const string TokenEndpoint = "https://oauth2.googleapis.com/token";
        const string Scope = "https://www.googleapis.com/auth/gmail.readonly";
        const string ApiBase = "https://gmail.googleapis.com/gmail/v1/users/me";

        static GmailClient()
        {
            // .NET 4 既定は古い TLS のことがある。Google は TLS 1.2 必須。
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        readonly object gate = new object();
        readonly object authLock = new object();
        HttpWebRequest inflight;
        string accessToken;
        string refreshToken;
        DateTime accessExpiresUtc = DateTime.MinValue;
        string lastError = "";
        GmailMailItem[] cache = new GmailMailItem[0];
        DateTime lastFetchUtc = DateTime.MinValue;

        public string LastError { get { lock (gate) return lastError; } }
        public bool IsAuthorized
        {
            get
            {
                lock (gate)
                    return !string.IsNullOrEmpty(refreshToken) || !string.IsNullOrEmpty(accessToken);
            }
        }

        public GmailMailItem[] Snapshot()
        {
            lock (gate)
            {
                var copy = new GmailMailItem[cache.Length];
                Array.Copy(cache, copy, cache.Length);
                return copy;
            }
        }

        public void LoadStoredToken()
        {
            string json = CredStore.Read(CredTarget);
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                TokenPayload t = FromJson<TokenPayload>(json);
                lock (gate)
                {
                    refreshToken = t.refresh_token;
                    accessToken = t.access_token;
                    if (t.expires_at_unix > 0)
                        accessExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                            .AddSeconds(t.expires_at_unix);
                }
            }
            catch { }
        }

        public void ClearTokens()
        {
            lock (gate)
            {
                accessToken = null;
                refreshToken = null;
                accessExpiresUtc = DateTime.MinValue;
                cache = new GmailMailItem[0];
                lastError = "";
            }
            CredStore.Delete(CredTarget);
        }

        /// <summary>表示を消したときに、進行中のトークン更新・メール取得を止める。</summary>
        public void AbortRequests()
        {
            HttpWebRequest req;
            lock (gate) req = inflight;
            if (req != null) try { req.Abort(); } catch { }
        }

        public bool AuthorizeInteractive(string clientId, string clientSecret, CancellationToken cancel)
        {
            // 前回の待受を閉じ切ってから次を開く。重ねると 127.0.0.1 が拒否される。
            lock (authLock)
            {
                if (string.IsNullOrEmpty(clientId))
                {
                    SetError("Gmail Client ID が未設定です");
                    return false;
                }

                string verifier = MakeVerifier();
                string challenge = MakeChallenge(verifier);
                LoopbackOAuthServer server;
                try { server = new LoopbackOAuthServer(); }
                catch (Exception ex)
                {
                    SetError("ローカル待受の開始に失敗しました: " + ex.Message);
                    return false;
                }

                using (server)
                {
                    string redirect = "http://127.0.0.1:" + server.Port + "/";
                    string state = MakeVerifier();
                    string url = AuthEndpoint
                        + "?client_id=" + Uri.EscapeDataString(clientId)
                        + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                        + "&response_type=code"
                        + "&scope=" + Uri.EscapeDataString(Scope)
                        + "&access_type=offline"
                        + "&prompt=consent"
                        + "&state=" + Uri.EscapeDataString(state)
                        + "&code_challenge=" + Uri.EscapeDataString(challenge)
                        + "&code_challenge_method=S256";

                    try { System.Diagnostics.Process.Start(url); }
                    catch (Exception ex)
                    {
                        SetError("ブラウザを開けません: " + ex.Message);
                        return false;
                    }

                    string code;
                    string err;
                    if (!server.WaitForCode(state, cancel, 180000, out code, out err))
                    {
                        SetError(cancel.IsCancellationRequested ? "キャンセルされました"
                            : (string.IsNullOrEmpty(err) ? "認証がタイムアウトしました" : err));
                        return false;
                    }
                    return ExchangeCode(clientId, clientSecret, code, redirect, verifier);
                }
            }
        }

        /// <summary>stillWanted が false なら API を叩かず、トークンも更新しない。</summary>
        public bool RefreshInbox(string clientId, string clientSecret, int count, Func<bool> stillWanted)
        {
            count = Math.Max(3, Math.Min(10, count));
            try
            {
                if (stillWanted != null && !stillWanted()) return false;
                if (!EnsureAccessToken(clientId, clientSecret)) return false;
                if (stillWanted != null && !stillWanted()) return false;
                string listJson = HttpGet(ApiBase + "/messages?labelIds=INBOX&maxResults=" + count);
                if (stillWanted != null && !stillWanted()) return false;
                MessageList list = FromJson<MessageList>(listJson);
                var items = new List<GmailMailItem>();
                if (list != null && list.messages != null)
                {
                    foreach (MessageRef mr in list.messages)
                    {
                        if (stillWanted != null && !stillWanted()) return false;
                        if (mr == null || string.IsNullOrEmpty(mr.id)) continue;
                        string detail = HttpGet(ApiBase + "/messages/" + Uri.EscapeDataString(mr.id)
                            + "?format=metadata&metadataHeaders=From&metadataHeaders=Subject");
                        MessageDetail md = FromJson<MessageDetail>(detail);
                        items.Add(ParseHeaders(md));
                    }
                }
                if (stillWanted != null && !stillWanted()) return false;
                lock (gate)
                {
                    cache = items.ToArray();
                    lastFetchUtc = DateTime.UtcNow;
                    lastError = "";
                }
                return true;
            }
            catch (Exception ex)
            {
                if (stillWanted != null && !stillWanted()) return false;
                SetError(ex.Message);
                return false;
            }
        }

        bool ExchangeCode(string clientId, string clientSecret, string code, string redirect, string verifier)
        {
            try
            {
                var body = new StringBuilder();
                body.Append("code=").Append(Uri.EscapeDataString(code));
                body.Append("&client_id=").Append(Uri.EscapeDataString(clientId));
                if (!string.IsNullOrEmpty(clientSecret))
                    body.Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret));
                body.Append("&redirect_uri=").Append(Uri.EscapeDataString(redirect));
                body.Append("&grant_type=authorization_code");
                body.Append("&code_verifier=").Append(Uri.EscapeDataString(verifier));
                string json = HttpPost(TokenEndpoint, body.ToString());
                return ApplyTokenJson(json, true);
            }
            catch (Exception ex)
            {
                SetError("トークン交換に失敗: " + ex.Message);
                return false;
            }
        }

        bool EnsureAccessToken(string clientId, string clientSecret)
        {
            lock (gate)
            {
                if (!string.IsNullOrEmpty(accessToken) && DateTime.UtcNow < accessExpiresUtc.AddMinutes(-2))
                    return true;
            }
            string rt;
            lock (gate) rt = refreshToken;
            if (string.IsNullOrEmpty(rt))
            {
                SetError("未接続です。メニューから Gmail に接続してください");
                return false;
            }
            try
            {
                var body = new StringBuilder();
                body.Append("client_id=").Append(Uri.EscapeDataString(clientId));
                if (!string.IsNullOrEmpty(clientSecret))
                    body.Append("&client_secret=").Append(Uri.EscapeDataString(clientSecret));
                body.Append("&refresh_token=").Append(Uri.EscapeDataString(rt));
                body.Append("&grant_type=refresh_token");
                string json = HttpPost(TokenEndpoint, body.ToString());
                return ApplyTokenJson(json, false);
            }
            catch (Exception ex)
            {
                SetError("トークン更新に失敗: " + ex.Message);
                return false;
            }
        }

        bool ApplyTokenJson(string json, bool requireRefresh)
        {
            TokenPayload t = FromJson<TokenPayload>(json);
            if (t == null || string.IsNullOrEmpty(t.access_token))
            {
                SetError("トークン応答が不正です");
                return false;
            }
            lock (gate)
            {
                accessToken = t.access_token;
                if (!string.IsNullOrEmpty(t.refresh_token)) refreshToken = t.refresh_token;
                else if (requireRefresh && string.IsNullOrEmpty(refreshToken))
                {
                    SetError("refresh_token が取得できませんでした");
                    return false;
                }
                int sec = t.expires_in > 0 ? t.expires_in : 3600;
                accessExpiresUtc = DateTime.UtcNow.AddSeconds(sec);
                PersistLocked();
                lastError = "";
            }
            return true;
        }

        void PersistLocked()
        {
            var t = new TokenPayload();
            t.access_token = accessToken;
            t.refresh_token = refreshToken;
            t.expires_at_unix = (long)(accessExpiresUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            CredStore.Write(CredTarget, ToJson(t));
        }

        static GmailMailItem ParseHeaders(MessageDetail md)
        {
            var item = new GmailMailItem();
            item.Subject = "(件名なし)";
            item.From = "(不明)";
            if (md != null && md.payload != null && md.payload.headers != null)
            {
                foreach (MsgHeader h in md.payload.headers)
                {
                    if (h == null || h.name == null) continue;
                    if (string.Equals(h.name, "Subject", StringComparison.OrdinalIgnoreCase))
                        item.Subject = string.IsNullOrEmpty(h.value) ? "(件名なし)" : h.value;
                    else if (string.Equals(h.name, "From", StringComparison.OrdinalIgnoreCase))
                        item.From = SimplifyFrom(h.value);
                }
            }
            return item;
        }

        static string SimplifyFrom(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(不明)";
            int lt = raw.IndexOf('<');
            if (lt > 0)
            {
                string name = raw.Substring(0, lt).Trim().Trim('"');
                if (name.Length > 0) return name;
            }
            return raw.Trim();
        }

        string HttpGet(string url)
        {
            string token;
            lock (gate) token = accessToken;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers["Authorization"] = "Bearer " + token;
            req.Timeout = 20000;
            return SendTracked(req, null);
        }

        string HttpPost(string url, string formBody)
        {
            byte[] data = Encoding.UTF8.GetBytes(formBody);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = 20000;
            req.ContentLength = data.Length;
            return SendTracked(req, data);
        }

        string SendTracked(HttpWebRequest req, byte[] body)
        {
            lock (gate) inflight = req;
            try
            {
                if (body != null)
                    using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                return ReadResponse(req);
            }
            finally
            {
                lock (gate) if (inflight == req) inflight = null;
            }
        }

        static string ReadResponse(HttpWebRequest req)
        {
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                string body = "";
                if (ex.Response != null)
                {
                    using (var sr = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        body = sr.ReadToEnd();
                }
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(body) ? ex.Message : Trunc(body, 180), ex);
            }
        }

        static string Trunc(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        static string HtmlEncode(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
        }

        void SetError(string msg)
        {
            lock (gate) lastError = msg ?? "";
        }

        static string ReadHttpRequest(NetworkStream ns)
        {
            var buf = new byte[4096];
            var sb = new StringBuilder();
            while (sb.ToString().IndexOf("\r\n\r\n") < 0)
            {
                int n = ns.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                if (sb.Length > 65536) break;
            }
            return sb.ToString();
        }

        static string FirstLine(string req)
        {
            if (string.IsNullOrEmpty(req)) return "";
            int i = req.IndexOf('\r');
            return i >= 0 ? req.Substring(0, i) : req;
        }

        static string MakeVerifier()
        {
            var bytes = new byte[32];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        static string MakeChallenge(string verifier)
        {
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            return Base64Url(hash);
        }

        static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(key)) return null;
            string q = query[0] == '?' ? query.Substring(1) : query;
            foreach (string part in q.Split('&'))
            {
                int eq = part.IndexOf('=');
                string k = eq >= 0 ? part.Substring(0, eq) : part;
                string v = eq >= 0 ? part.Substring(eq + 1) : "";
                if (string.Equals(Uri.UnescapeDataString(k.Replace('+', ' ')), key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return null;
        }

        static T FromJson<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return ser.ReadObject(ms) as T;
            }
        }

        static string ToJson<T>(T obj)
        {
            using (var ms = new MemoryStream())
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                ser.WriteObject(ms, obj);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        /// <summary>
        /// 127.0.0.1 で認可コードが届くまで待ち受ける。
        /// ブラウザやセキュリティソフトの先回り接続（favicon や空接続）では閉じない。
        /// </summary>
        sealed class LoopbackOAuthServer : IDisposable
        {
            readonly TcpListener listener;
            IAsyncResult pending;
            public readonly int Port;

            public LoopbackOAuthServer()
            {
                Exception last = null;
                TcpListener bound = null;
                for (int attempt = 0; attempt < 5 && bound == null; attempt++)
                {
                    var l = new TcpListener(IPAddress.Loopback, 0);
                    try
                    {
                        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        l.Start(16);
                        bound = l;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                        try { l.Stop(); } catch { }
                    }
                }
                if (bound == null) throw last ?? new IOException("127.0.0.1 で待受できません");
                listener = bound;
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public bool WaitForCode(string expectedState, CancellationToken cancel, int timeoutMs, out string code, out string error)
            {
                code = null;
                error = null;
                int start = Environment.TickCount;
                pending = listener.BeginAcceptTcpClient(null, null);
                try
                {
                    while (true)
                    {
                        if (cancel.IsCancellationRequested) return false;
                        int remain = timeoutMs - unchecked(Environment.TickCount - start);
                        if (remain <= 0) return false;
                        int idx = WaitHandle.WaitAny(
                            new WaitHandle[] { pending.AsyncWaitHandle, cancel.WaitHandle }, remain);
                        if (idx != 0) return false;
                        TcpClient client;
                        try { client = listener.EndAcceptTcpClient(pending); }
                        catch { return false; }
                        pending = null;
                        bool done = HandleClient(client, expectedState, out code, out error);
                        if (done) return code != null;
                        pending = listener.BeginAcceptTcpClient(null, null);
                    }
                }
                finally { Dispose(); }
            }

            bool HandleClient(TcpClient client, string expectedState, out string code, out string error)
            {
                code = null;
                error = null;
                using (client)
                {
                    try { client.LingerState = new LingerOption(true, 0); } catch { }
                    string method = "GET", query = "";
                    try
                    {
                        client.ReceiveTimeout = 1000;
                        using (NetworkStream ns = client.GetStream())
                        {
                            string req = ReadHttpRequest(ns);
                            string first = FirstLine(req);
                            string[] parts = first.Split(' ');
                            if (parts.Length > 0 && parts[0].Length > 0) method = parts[0];
                            string path = parts.Length > 1 ? parts[1] : "";
                            int qmark = path.IndexOf('?');
                            query = qmark >= 0 ? path.Substring(qmark) : "";
                            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                            {
                                WriteHttp(ns, 204, "No Content", "");
                                return false;
                            }
                            string gotCode = QueryValue(query, "code");
                            string gotErr = QueryValue(query, "error");
                            string gotState = QueryValue(query, "state");
                            bool oauth = gotCode != null || gotErr != null || gotState != null;
                            if (!oauth)
                            {
                                WriteHttp(ns, 200, "OK", "");
                                return false;
                            }
                            bool stateOk = string.Equals(expectedState, gotState, StringComparison.Ordinal);
                            string msg;
                            if (!string.IsNullOrEmpty(gotErr))
                            {
                                error = "認証エラー: " + Trunc(gotErr, 120);
                                msg = "認証に失敗しました: " + HtmlEncode(gotErr);
                            }
                            else if (!stateOk)
                            {
                                error = "認証エラー: state が一致しません";
                                msg = "認証に失敗しました: state が一致しません";
                            }
                            else if (string.IsNullOrEmpty(gotCode))
                            {
                                error = "認証エラー: 認可コードがありません";
                                msg = "認証に失敗しました: 認可コードがありません";
                            }
                            else
                            {
                                code = gotCode;
                                msg = "認証できました。このタブを閉じてウィジェットに戻ってください。";
                            }
                            string html = "<html><body style='font-family:sans-serif;padding:24px'>" + msg + "</body></html>";
                            WriteHttp(ns, 200, "OK", html);
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }
            }

            static void WriteHttp(NetworkStream ns, int status, string reason, string body)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
                string header = "HTTP/1.1 " + status + " " + reason + "\r\n"
                    + "Content-Type: text/html; charset=utf-8\r\n"
                    + "Content-Length: " + bytes.Length + "\r\n"
                    + "Access-Control-Allow-Origin: *\r\n"
                    + "Access-Control-Allow-Private-Network: true\r\n"
                    + "Access-Control-Allow-Methods: GET, OPTIONS\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(header);
                ns.Write(hb, 0, hb.Length);
                if (bytes.Length > 0) ns.Write(bytes, 0, bytes.Length);
            }

            public void Dispose()
            {
                try { listener.Stop(); } catch { }
                IAsyncResult ar = pending;
                pending = null;
                if (ar == null) return;
                try { if (!ar.IsCompleted) ar.AsyncWaitHandle.WaitOne(200); } catch { }
                try { using (listener.EndAcceptTcpClient(ar)) { } } catch { }
            }
        }

        internal static int RunLoopbackSelfTest()
        {
            int fails = 0;
            fails += LoopbackRound(0, true);
            fails += LoopbackRound(1, true);
            fails += LoopbackRound(2, false);
            fails += CancelRound();
            fails += LoopbackRound(3, true);
            return fails;
        }

        static int CancelRound()
        {
            var cts = new CancellationTokenSource();
            var server = new LoopbackOAuthServer();
            bool ok = true;
            string code = "x";
            var worker = new Thread(() =>
            {
                string err;
                ok = server.WaitForCode("x", cts.Token, 10000, out code, out err);
            });
            worker.IsBackground = true;
            worker.Start();
            Thread.Sleep(150);
            cts.Cancel();
            if (!worker.Join(3000))
            {
                try { server.Dispose(); } catch { }
                return 1;
            }
            return (ok || code != null) ? 1 : 0;
        }

        static int LoopbackRound(int n, bool withProbe)
        {
            var server = new LoopbackOAuthServer();
            string state = "state" + n;
            string got = null;
            string err = null;
            bool ok = false;
            var worker = new Thread(() =>
            {
                ok = server.WaitForCode(state, CancellationToken.None, 8000, out got, out err);
            });
            worker.IsBackground = true;
            worker.Start();
            Thread.Sleep(80);
            if (withProbe)
            {
                try
                {
                    var idle = new TcpClient("127.0.0.1", server.Port);
                    HttpSend(server.Port, "GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
                    Thread.Sleep(50);
                    idle.Close();
                }
                catch { }
            }
            HttpSend(server.Port, "GET /?code=code" + n + "&state=" + state
                + " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n");
            if (!worker.Join(5000)) { try { server.Dispose(); } catch { } return 1; }
            if (!ok || got != "code" + n || !string.IsNullOrEmpty(err)) return 1;
            return 0;
        }

        static void HttpSend(int port, string request)
        {
            using (var c = new TcpClient("127.0.0.1", port))
            using (NetworkStream ns = c.GetStream())
            {
                byte[] b = Encoding.ASCII.GetBytes(request);
                ns.Write(b, 0, b.Length);
                ns.ReadTimeout = 2000;
                var buf = new byte[512];
                try { ns.Read(buf, 0, buf.Length); } catch { }
            }
        }

        [DataContract]
        class TokenPayload
        {
            [DataMember] public string access_token;
            [DataMember] public string refresh_token;
            [DataMember] public int expires_in;
            [DataMember] public long expires_at_unix;
        }

        [DataContract]
        class MessageList
        {
            [DataMember] public MessageRef[] messages;
        }

        [DataContract]
        class MessageRef
        {
            [DataMember] public string id;
        }

        [DataContract]
        class MessageDetail
        {
            [DataMember] public MsgPayload payload;
        }

        [DataContract]
        class MsgPayload
        {
            [DataMember] public MsgHeader[] headers;
        }

        [DataContract]
        class MsgHeader
        {
            [DataMember] public string name;
            [DataMember] public string value;
        }
    }

    /// <summary>
    /// OAuth の Client ID / Secret。settings.ini ではなく Credential Manager に置く。
    /// </summary>
    static class GmailAppCredentials
    {
        const string Target = "SysmonWidget:GmailOAuthClient";

        public static void Save(string clientId, string clientSecret)
        {
            var t = new ClientPayload();
            t.client_id = clientId ?? "";
            t.client_secret = clientSecret ?? "";
            CredStore.Write(Target, Encode(t));
        }

        public static void Load(out string clientId, out string clientSecret)
        {
            clientId = "";
            clientSecret = "";
            string json = CredStore.Read(Target);
            if (string.IsNullOrEmpty(json)) return;
            try
            {
                ClientPayload t = Decode(json);
                if (t == null) return;
                clientId = t.client_id ?? "";
                clientSecret = t.client_secret ?? "";
            }
            catch { }
        }

        public static void Delete()
        {
            CredStore.Delete(Target);
        }

        static string Encode(ClientPayload t)
        {
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(ClientPayload)).WriteObject(ms, t);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static ClientPayload Decode(string json)
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return new DataContractJsonSerializer(typeof(ClientPayload)).ReadObject(ms) as ClientPayload;
        }

        [DataContract]
        class ClientPayload
        {
            [DataMember] public string client_id;
            [DataMember] public string client_secret;
        }
    }

    static class CredStore
    {
        const int CredTypeGeneric = 1;
        // このユーザーのログオン間で永続（同一マシン）。中身は DPAPI CurrentUser で保護する。
        const int CredPersistLocalMachine = 2;
        static readonly byte[] DpapiEntropy = Encoding.UTF8.GetBytes("SysmonWidget.Gmail.v1");

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref CREDENTIAL userCredential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CredFree(IntPtr buffer);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredDelete(string target, int type, int flags);

        public static void Write(string target, string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(value ?? "");
            byte[] bytes = Protect(plain);
            IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var c = new CREDENTIAL();
                c.Type = CredTypeGeneric;
                c.TargetName = target;
                c.CredentialBlobSize = bytes.Length;
                c.CredentialBlob = blob;
                c.Persist = CredPersistLocalMachine;
                c.UserName = Environment.UserName;
                if (!CredWrite(ref c, 0))
                    throw new InvalidOperationException("CredWrite failed: " + Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(blob); }
        }

        public static string Read(string target)
        {
            IntPtr ptr;
            if (!CredRead(target, CredTypeGeneric, 0, out ptr) || ptr == IntPtr.Zero) return null;
            try
            {
                var c = (CREDENTIAL)Marshal.PtrToStructure(ptr, typeof(CREDENTIAL));
                if (c.CredentialBlob == IntPtr.Zero || c.CredentialBlobSize <= 0) return null;
                var bytes = new byte[c.CredentialBlobSize];
                Marshal.Copy(c.CredentialBlob, bytes, 0, bytes.Length);
                byte[] plain = UnprotectOrLegacy(bytes);
                return Encoding.UTF8.GetString(plain);
            }
            finally { CredFree(ptr); }
        }

        public static void Delete(string target)
        {
            CredDelete(target, CredTypeGeneric, 0);
        }

        static byte[] Protect(byte[] plain)
        {
            return ProtectedData.Protect(plain, DpapiEntropy, DataProtectionScope.CurrentUser);
        }

        static byte[] UnprotectOrLegacy(byte[] bytes)
        {
            try
            {
                return ProtectedData.Unprotect(bytes, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // v1.2.2 以前の平文 Credential Manager エントリ
                return bytes;
            }
        }
    }
}
