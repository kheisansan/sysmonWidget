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
    /// Client ID / Secret はユーザーが Google Cloud で作って settings に入れる。
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

        public bool AuthorizeInteractive(string clientId, string clientSecret, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                SetError("Gmail Client ID が未設定です");
                return false;
            }

            string verifier = MakeVerifier();
            string challenge = MakeChallenge(verifier);
            TcpListener listener = null;
            int port = 0;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (Exception ex)
            {
                SetError("ローカル待受の開始に失敗しました: " + ex.Message);
                return false;
            }

            string redirect = "http://127.0.0.1:" + port + "/";
            string url = AuthEndpoint
                + "?client_id=" + Uri.EscapeDataString(clientId)
                + "&redirect_uri=" + Uri.EscapeDataString(redirect)
                + "&response_type=code"
                + "&scope=" + Uri.EscapeDataString(Scope)
                + "&access_type=offline"
                + "&prompt=consent"
                + "&code_challenge=" + Uri.EscapeDataString(challenge)
                + "&code_challenge_method=S256";

            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                try { listener.Stop(); } catch { }
                SetError("ブラウザを開けません: " + ex.Message);
                return false;
            }

            string code = null;
            try
            {
                IAsyncResult ar = listener.BeginAcceptTcpClient(null, null);
                int idx = WaitHandle.WaitAny(new WaitHandle[] { ar.AsyncWaitHandle, cancel.WaitHandle }, 180000);
                if (idx != 0)
                {
                    SetError(cancel.IsCancellationRequested ? "キャンセルされました" : "認証がタイムアウトしました");
                }
                else
                {
                    using (TcpClient client = listener.EndAcceptTcpClient(ar))
                    using (NetworkStream ns = client.GetStream())
                    {
                        ns.ReadTimeout = 10000;
                        string req = ReadHttpRequest(ns);
                        string first = FirstLine(req);
                        string pathQuery = first.Split(' ').Length > 1 ? first.Split(' ')[1] : "";
                        int qmark = pathQuery.IndexOf('?');
                        string q = qmark >= 0 ? pathQuery.Substring(qmark) : "";
                        code = QueryValue(q, "code");
                        string err = QueryValue(q, "error");
                        string html = "<html><body style='font-family:sans-serif;padding:24px'>"
                            + (string.IsNullOrEmpty(code)
                                ? ("認証に失敗しました: " + (err ?? "unknown"))
                                : "認証できました。このタブを閉じてウィジェットに戻ってください。")
                            + "</body></html>";
                        byte[] body = Encoding.UTF8.GetBytes(html);
                        string header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: "
                            + body.Length + "\r\nConnection: close\r\n\r\n";
                        byte[] hb = Encoding.ASCII.GetBytes(header);
                        ns.Write(hb, 0, hb.Length);
                        ns.Write(body, 0, body.Length);
                        if (!string.IsNullOrEmpty(err))
                        {
                            SetError("認証エラー: " + err);
                            code = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SetError("認証待受エラー: " + ex.Message);
                code = null;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }

            if (string.IsNullOrEmpty(code)) return false;
            return ExchangeCode(clientId, clientSecret, code, redirect, verifier);
        }

        public bool RefreshInbox(string clientId, string clientSecret, int count)
        {
            count = Math.Max(3, Math.Min(10, count));
            try
            {
                if (!EnsureAccessToken(clientId, clientSecret)) return false;
                string listJson = HttpGet(ApiBase + "/messages?labelIds=INBOX&maxResults=" + count);
                MessageList list = FromJson<MessageList>(listJson);
                var items = new List<GmailMailItem>();
                if (list != null && list.messages != null)
                {
                    foreach (MessageRef mr in list.messages)
                    {
                        if (mr == null || string.IsNullOrEmpty(mr.id)) continue;
                        string detail = HttpGet(ApiBase + "/messages/" + Uri.EscapeDataString(mr.id)
                            + "?format=metadata&metadataHeaders=From&metadataHeaders=Subject");
                        MessageDetail md = FromJson<MessageDetail>(detail);
                        items.Add(ParseHeaders(md));
                    }
                }
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
            return ReadResponse(req);
        }

        static string HttpPost(string url, string formBody)
        {
            byte[] data = Encoding.UTF8.GetBytes(formBody);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.Timeout = 20000;
            req.ContentLength = data.Length;
            using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
            return ReadResponse(req);
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

    static class CredStore
    {
        const int CredTypeGeneric = 1;
        const int CredPersistLocalMachine = 2;

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
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
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
                return Encoding.UTF8.GetString(bytes);
            }
            finally { CredFree(ptr); }
        }

        public static void Delete(string target)
        {
            CredDelete(target, CredTypeGeneric, 0);
        }
    }
}
