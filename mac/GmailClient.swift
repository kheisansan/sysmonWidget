import AppKit
import CommonCrypto
import Foundation
import Security

struct GmailMailItem {
    var from: String = ""
    var subject: String = ""
}

/// Gmail API (readonly) + デスクトップ OAuth (PKCE)。トークンは Keychain に保存。
final class GmailClient {
    private static let keychainService = "com.sysmonwidget.mac.gmail"
    private static let keychainAccount = "oauth"
    private static let authEndpoint = "https://accounts.google.com/o/oauth2/v2/auth"
    private static let tokenEndpoint = "https://oauth2.googleapis.com/token"
    private static let scope = "https://www.googleapis.com/auth/gmail.readonly"
    private static let apiBase = "https://gmail.googleapis.com/gmail/v1/users/me"

    private let lock = NSLock()
    private var accessToken: String?
    private var refreshToken: String?
    private var accessExpires = Date.distantPast
    private(set) var lastError = ""
    private var cache: [GmailMailItem] = []

    var isAuthorized: Bool {
        lock.lock(); defer { lock.unlock() }
        return !(refreshToken ?? "").isEmpty || !(accessToken ?? "").isEmpty
    }

    func snapshot() -> [GmailMailItem] {
        lock.lock(); defer { lock.unlock() }
        return cache
    }

    func loadStoredToken() {
        guard let data = Keychain.get(service: Self.keychainService, account: Self.keychainAccount),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return }
        lock.lock()
        accessToken = obj["access_token"] as? String
        refreshToken = obj["refresh_token"] as? String
        if let unix = obj["expires_at_unix"] as? Double {
            accessExpires = Date(timeIntervalSince1970: unix)
        }
        lock.unlock()
    }

    func clearTokens() {
        lock.lock()
        accessToken = nil
        refreshToken = nil
        accessExpires = .distantPast
        cache = []
        lastError = ""
        lock.unlock()
        Keychain.delete(service: Self.keychainService, account: Self.keychainAccount)
    }

    func authorizeInteractive(clientId: String, clientSecret: String) -> Bool {
        if clientId.isEmpty {
            setError("Gmail Client ID が未設定です")
            return false
        }
        guard let listener = LoopbackServer() else {
            setError("ローカル待受の開始に失敗しました")
            return false
        }
        let redirect = "http://127.0.0.1:\(listener.port)/"
        let verifier = Self.makeVerifier()
        let challenge = Self.makeChallenge(verifier)
        let state = Self.makeVerifier()
        var comps = URLComponents(string: Self.authEndpoint)!
        comps.queryItems = [
            URLQueryItem(name: "client_id", value: clientId),
            URLQueryItem(name: "redirect_uri", value: redirect),
            URLQueryItem(name: "response_type", value: "code"),
            URLQueryItem(name: "scope", value: Self.scope),
            URLQueryItem(name: "access_type", value: "offline"),
            URLQueryItem(name: "prompt", value: "consent"),
            URLQueryItem(name: "state", value: state),
            URLQueryItem(name: "code_challenge", value: challenge),
            URLQueryItem(name: "code_challenge_method", value: "S256")
        ]
        guard let url = comps.url else {
            listener.close()
            setError("認証 URL の生成に失敗しました")
            return false
        }
        DispatchQueue.main.async {
            NSWorkspace.shared.open(url)
        }

        guard let req = listener.acceptHTTP(timeout: 180) else {
            listener.close()
            setError("認証がタイムアウトしました")
            return false
        }
        listener.close()

        let code = req.query["code"]
        let err = req.query["error"]
        let gotState = req.query["state"]
        let stateOk = gotState == state
        let html: String
        if let err, !err.isEmpty {
            html = "<html><body style='font-family:sans-serif;padding:24px'>認証に失敗しました: \(Self.htmlEncode(err))</body></html>"
            _ = req.respond(html)
            setError("認証エラー: \(String(err.prefix(120)))")
            return false
        }
        if !stateOk {
            html = "<html><body style='font-family:sans-serif;padding:24px'>認証に失敗しました: state が一致しません</body></html>"
            _ = req.respond(html)
            setError("認証エラー: state が一致しません")
            return false
        }
        if let code, !code.isEmpty {
            html = "<html><body style='font-family:sans-serif;padding:24px'>認証できました。このタブを閉じてウィジェットに戻ってください。</body></html>"
            _ = req.respond(html)
            return exchangeCode(clientId: clientId, clientSecret: clientSecret, code: code, redirect: redirect, verifier: verifier)
        }
        html = "<html><body style='font-family:sans-serif;padding:24px'>認証に失敗しました: 認可コードがありません</body></html>"
        _ = req.respond(html)
        setError("認証エラー: 認可コードがありません")
        return false
    }

    func refreshInbox(clientId: String, clientSecret: String, count: Int) -> Bool {
        let count = min(10, max(3, count))
        do {
            try ensureAccessToken(clientId: clientId, clientSecret: clientSecret)
            let listData = try httpGet("\(Self.apiBase)/messages?labelIds=INBOX&maxResults=\(count)")
            let list = try JSONSerialization.jsonObject(with: listData) as? [String: Any]
            let messages = list?["messages"] as? [[String: Any]] ?? []
            var items: [GmailMailItem] = []
            for msg in messages {
                guard let id = msg["id"] as? String else { continue }
                let encId = id.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? id
                let detailData = try httpGet("\(Self.apiBase)/messages/\(encId)?format=metadata&metadataHeaders=From&metadataHeaders=Subject")
                let detail = try JSONSerialization.jsonObject(with: detailData) as? [String: Any]
                items.append(Self.parseHeaders(detail))
            }
            lock.lock()
            cache = items
            lastError = ""
            lock.unlock()
            return true
        } catch {
            setError(error.localizedDescription)
            return false
        }
    }

    private func exchangeCode(clientId: String, clientSecret: String, code: String, redirect: String, verifier: String) -> Bool {
        var body = "code=\(Self.esc(code))&client_id=\(Self.esc(clientId))"
            + "&redirect_uri=\(Self.esc(redirect))&grant_type=authorization_code&code_verifier=\(Self.esc(verifier))"
        if !clientSecret.isEmpty {
            body += "&client_secret=\(Self.esc(clientSecret))"
        }
        do {
            let data = try httpPost(Self.tokenEndpoint, body: body)
            return applyTokenJSON(data, requireRefresh: true)
        } catch {
            setError("トークン交換に失敗: \(error.localizedDescription)")
            return false
        }
    }

    private func ensureAccessToken(clientId: String, clientSecret: String) throws {
        lock.lock()
        let access = accessToken
        let expires = accessExpires
        let refresh = refreshToken
        lock.unlock()
        if let access, !access.isEmpty, Date() < expires.addingTimeInterval(-120) {
            return
        }
        guard let refresh, !refresh.isEmpty else {
            throw NSError(domain: "Gmail", code: 1, userInfo: [NSLocalizedDescriptionKey: "未接続です。メニューから Gmail に接続してください"])
        }
        var body = "client_id=\(Self.esc(clientId))&refresh_token=\(Self.esc(refresh))&grant_type=refresh_token"
        if !clientSecret.isEmpty {
            body += "&client_secret=\(Self.esc(clientSecret))"
        }
        let data = try httpPost(Self.tokenEndpoint, body: body)
        if !applyTokenJSON(data, requireRefresh: false) {
            throw NSError(domain: "Gmail", code: 2, userInfo: [NSLocalizedDescriptionKey: lastError])
        }
    }

    private func applyTokenJSON(_ data: Data, requireRefresh: Bool) -> Bool {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let access = obj["access_token"] as? String, !access.isEmpty else {
            setError("トークン応答が不正です")
            return false
        }
        lock.lock()
        accessToken = access
        if let rt = obj["refresh_token"] as? String, !rt.isEmpty {
            refreshToken = rt
        } else if requireRefresh && (refreshToken ?? "").isEmpty {
            lock.unlock()
            setError("refresh_token が取得できませんでした")
            return false
        }
        let sec = (obj["expires_in"] as? Double) ?? Double(obj["expires_in"] as? Int ?? 3600)
        accessExpires = Date().addingTimeInterval(sec)
        persistLocked()
        lastError = ""
        lock.unlock()
        return true
    }

    private func persistLocked() {
        var obj: [String: Any] = [:]
        if let accessToken { obj["access_token"] = accessToken }
        if let refreshToken { obj["refresh_token"] = refreshToken }
        obj["expires_at_unix"] = accessExpires.timeIntervalSince1970
        if let data = try? JSONSerialization.data(withJSONObject: obj) {
            Keychain.set(data, service: Self.keychainService, account: Self.keychainAccount)
        }
    }

    private func httpGet(_ url: String) throws -> Data {
        lock.lock(); let token = accessToken; lock.unlock()
        var req = URLRequest(url: URL(string: url)!, timeoutInterval: 20)
        req.setValue("Bearer \(token ?? "")", forHTTPHeaderField: "Authorization")
        return try syncData(req)
    }

    private func httpPost(_ url: String, body: String) throws -> Data {
        var req = URLRequest(url: URL(string: url)!, timeoutInterval: 20)
        req.httpMethod = "POST"
        req.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
        req.httpBody = body.data(using: .utf8)
        return try syncData(req)
    }

    private func syncData(_ req: URLRequest) throws -> Data {
        let sem = DispatchSemaphore(value: 0)
        var outData: Data?
        var outError: Error?
        let task = URLSession.shared.dataTask(with: req) { data, resp, err in
            if let err { outError = err }
            else if let http = resp as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
                let msg = String(data: data ?? Data(), encoding: .utf8) ?? "HTTP \(http.statusCode)"
                outError = NSError(domain: "Gmail", code: http.statusCode,
                                   userInfo: [NSLocalizedDescriptionKey: String(msg.prefix(180))])
            } else {
                outData = data
            }
            sem.signal()
        }
        task.resume()
        _ = sem.wait(timeout: .now() + 25)
        if let outError { throw outError }
        guard let outData else {
            throw NSError(domain: "Gmail", code: -1, userInfo: [NSLocalizedDescriptionKey: "応答なし"])
        }
        return outData
    }

    private func setError(_ msg: String) {
        lock.lock(); lastError = msg; lock.unlock()
    }

    private static func parseHeaders(_ detail: [String: Any]?) -> GmailMailItem {
        var item = GmailMailItem(from: "(不明)", subject: "(件名なし)")
        guard let payload = detail?["payload"] as? [String: Any],
              let headers = payload["headers"] as? [[String: Any]] else { return item }
        for h in headers {
            let name = (h["name"] as? String) ?? ""
            let value = (h["value"] as? String) ?? ""
            if name.caseInsensitiveCompare("Subject") == .orderedSame {
                item.subject = value.isEmpty ? "(件名なし)" : value
            } else if name.caseInsensitiveCompare("From") == .orderedSame {
                item.from = simplifyFrom(value)
            }
        }
        return item
    }

    private static func simplifyFrom(_ raw: String) -> String {
        if raw.isEmpty { return "(不明)" }
        if let lt = raw.firstIndex(of: "<") {
            let name = raw[..<lt].trimmingCharacters(in: .whitespacesAndNewlines)
                .trimmingCharacters(in: CharacterSet(charactersIn: "\""))
            if !name.isEmpty { return name }
        }
        return raw.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func makeVerifier() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        return base64URL(Data(bytes))
    }

    private static func makeChallenge(_ verifier: String) -> String {
        let data = Data(verifier.utf8)
        var hash = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
        data.withUnsafeBytes { ptr in
            _ = CC_SHA256(ptr.baseAddress, CC_LONG(data.count), &hash)
        }
        return base64URL(Data(hash))
    }

    private static func base64URL(_ data: Data) -> String {
        data.base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// application/x-www-form-urlencoded（RFC 3986 unreserved 以外をエンコード）
    private static func esc(_ s: String) -> String {
        var allowed = CharacterSet.alphanumerics
        allowed.insert(charactersIn: "-._~")
        return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? ""
    }

    private static func htmlEncode(_ s: String) -> String {
        s
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }
}

enum Keychain {
    static func set(_ data: Data, service: String, account: String) {
        delete(service: service, account: account)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        ]
        SecItemAdd(query as CFDictionary, nil)
    }

    static func get(service: String, account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne
        ]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        guard status == errSecSuccess else { return nil }
        return item as? Data
    }

    static func delete(service: String, account: String) {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
        SecItemDelete(query as CFDictionary)
    }
}

/// OAuth の Client ID / Secret。settings.ini ではなく Keychain に置く。
enum GmailAppCredentials {
    private static let service = "com.sysmonwidget.mac.gmail"
    private static let account = "oauth-client"

    static func save(clientId: String, clientSecret: String) {
        let obj: [String: String] = [
            "client_id": clientId,
            "client_secret": clientSecret
        ]
        guard let data = try? JSONSerialization.data(withJSONObject: obj) else { return }
        Keychain.set(data, service: service, account: account)
    }

    static func load() -> (clientId: String, clientSecret: String) {
        guard let data = Keychain.get(service: service, account: account),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ("", "")
        }
        return (obj["client_id"] as? String ?? "", obj["client_secret"] as? String ?? "")
    }

    static func delete() {
        Keychain.delete(service: service, account: account)
    }
}

private final class LoopbackServer {
    let port: Int
    private let fd: Int32

    init?() {
        let sock = socket(AF_INET, SOCK_STREAM, 0)
        guard sock >= 0 else { return nil }
        var yes: Int32 = 1
        setsockopt(sock, SOL_SOCKET, SO_REUSEADDR, &yes, socklen_t(MemoryLayout.size(ofValue: yes)))
        var addr = sockaddr_in()
        addr.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        addr.sin_family = sa_family_t(AF_INET)
        addr.sin_port = 0
        addr.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))
        let bindOk: Bool = withUnsafePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                bind(sock, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0
            }
        }
        guard bindOk, listen(sock, 1) == 0 else {
            Darwin.close(sock)
            return nil
        }
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        var got = sockaddr_in()
        withUnsafeMutablePointer(to: &got) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                _ = getsockname(sock, $0, &len)
            }
        }
        fd = sock
        port = Int(UInt16(bigEndian: got.sin_port))
    }

    func close() { Darwin.close(fd) }

    struct Request {
        let query: [String: String]
        let client: Int32
        func respond(_ html: String) -> Bool {
            let body = Array(html.utf8)
            let header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
            let bytes = Array(header.utf8) + body
            let n = bytes.withUnsafeBufferPointer { write(client, $0.baseAddress!, $0.count) }
            Darwin.close(client)
            return n > 0
        }
    }

    func acceptHTTP(timeout: Int) -> Request? {
        var tv = timeval(tv_sec: timeout, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout.size(ofValue: tv)))
        var addr = sockaddr_in()
        var len = socklen_t(MemoryLayout<sockaddr_in>.size)
        let client: Int32 = withUnsafeMutablePointer(to: &addr) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                accept(fd, $0, &len)
            }
        }
        guard client >= 0 else { return nil }
        var buf = [UInt8](repeating: 0, count: 4096)
        var raw = ""
        while !raw.contains("\r\n\r\n") {
            let n = read(client, &buf, buf.count)
            if n <= 0 { break }
            raw += String(bytes: buf[0..<n], encoding: .ascii) ?? ""
            if raw.count > 65536 { break }
        }
        let first = raw.split(separator: "\r\n", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
        let parts = first.split(separator: " ")
        let path = parts.count > 1 ? String(parts[1]) : ""
        let qIndex = path.firstIndex(of: "?")
        let q = qIndex.map { String(path[$0...]) } ?? ""
        return Request(query: Self.parseQuery(q), client: client)
    }

    private static func parseQuery(_ q: String) -> [String: String] {
        var out: [String: String] = [:]
        let s = q.hasPrefix("?") ? String(q.dropFirst()) : q
        for part in s.split(separator: "&") {
            let kv = part.split(separator: "=", maxSplits: 1).map(String.init)
            let k = kv.first?.removingPercentEncoding?.replacingOccurrences(of: "+", with: " ") ?? ""
            let v = kv.count > 1 ? (kv[1].removingPercentEncoding?.replacingOccurrences(of: "+", with: " ") ?? "") : ""
            if !k.isEmpty { out[k] = v }
        }
        return out
    }
}
