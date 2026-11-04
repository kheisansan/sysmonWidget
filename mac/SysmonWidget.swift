import AppKit
import CoreWLAN
import Darwin
import IOKit

// macOS 版 SysmonWidget。
// Intel / Apple Silicon は同じソースをユニバーサルバイナリにして両方で動かす。
// 数値の意味は Windows 版に合わせてある。
//   CPU  : 全コアの user+system+nice
//   MEM  : App + Wired + Compressed（キャッシュは含めない）
//   DISK : 一番忙しい物理ディスクの処理時間 / 経過時間
//   GPU  : IOAccelerator の使用率。複数枚あるときは最大
//   Wi-Fi: 接続中のインターフェイスの送受信バイト差分

enum Layout {
    static let histLen = 60
    static let baseWidth: CGFloat = 240
    static let minWidth: CGFloat = 190
    static let rightColMin: CGFloat = 168
    static let panelGap: CGFloat = 10
    static let pad: CGFloat = 12
    static let labelH: CGFloat = 18
    static let barH: CGFloat = 6
    static let rowGap: CGFloat = 10
    static let sepH: CGFloat = 15
    static let netHeaderH: CGFloat = 18
    static let netSpeedH: CGFloat = 22
    static let graphH: CGFloat = 36
    static let minGraphH: CGFloat = 20
    static let clockDigitalH: CGFloat = 56
    static let clockAnalogH: CGFloat = 120
    static let pomoH: CGFloat = 72
    static let gmailRowH: CGFloat = 34
    static let gmailHeaderH: CGFloat = 20
    static let resizeBorder: CGFloat = 6
    static let fontScales: [CGFloat] = [0.75, 0.9, 1.0, 1.15, 1.3, 1.5, 1.75, 2.0]
    static let workMins = [15, 20, 25, 30, 45, 50, 60]
    static let breakMins = [3, 5, 10, 15, 20]
    static let gmailIntervals = [15, 30, 60, 120, 300, 600]

    static func metricsContentHeight(_ graph: CGFloat) -> CGFloat {
        pad * 2 + 4 * (labelH + barH + rowGap) + sepH + netHeaderH + netSpeedH + graph
    }

    static func sidePanelsMinHeight(settings: Settings) -> CGFloat {
        var h = pad * 2
        if settings.showClock {
            h += (settings.clockAnalog ? clockAnalogH : clockDigitalH) + 8
        }
        if settings.showPomodoro { h += pomoH + 8 }
        if settings.showGmail { h += gmailHeaderH + CGFloat(settings.gmailCount) * gmailRowH + 8 }
        return max(h, metricsContentHeight(minGraphH))
    }
}

enum Palette {
    static let bg = c(28, 28, 32)
    static let label = c(170, 170, 180)
    static let value = c(240, 240, 245)
    static let dim = c(120, 120, 130)
    static let track = c(55, 55, 62)
    static let sep = c(60, 60, 68)
    static let cpu = c(77, 166, 255)
    static let mem = c(178, 120, 255)
    static let disk = c(80, 210, 140)
    static let gpu = c(255, 170, 60)
    static let high = c(240, 80, 90)
    static let down = c(77, 200, 255)
    static let up = c(255, 140, 80)
    static let graphBg = c(36, 36, 42)
    static let grip = c(90, 90, 100)

    static func c(_ r: Int, _ g: Int, _ b: Int, _ a: CGFloat = 1) -> NSColor {
        NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: a)
    }
}

enum Format {
    static func percent(_ v: Double) -> String {
        if v < 0 || v.isNaN { return "--" }
        return String(format: "%.0f%%", min(100, v))
    }

    static func speed(_ bytesPerSec: Double, bits: Bool) -> String {
        if bytesPerSec < 0 || bytesPerSec.isNaN { return "--" }
        if bits {
            let b = bytesPerSec * 8
            if b < 1e3 { return String(format: "%.0f bps", b) }
            if b < 1e6 { return String(format: "%.1f Kbps", b / 1e3) }
            if b < 1e9 { return String(format: "%.1f Mbps", b / 1e6) }
            return String(format: "%.2f Gbps", b / 1e9)
        }
        if bytesPerSec < 1024 { return String(format: "%.0f B/s", bytesPerSec) }
        if bytesPerSec < 1048576 { return String(format: "%.1f KB/s", bytesPerSec / 1024) }
        if bytesPerSec < 1073741824 { return String(format: "%.1f MB/s", bytesPerSec / 1048576) }
        return String(format: "%.2f GB/s", bytesPerSec / 1073741824)
    }

    /// ディスクの処理時間（ナノ秒）を、その区間の稼働率にする。
    static func busyPercent(serviceNs: UInt64, elapsedNs: UInt64) -> Double {
        if elapsedNs == 0 { return -1 }
        let pct = Double(serviceNs) / Double(elapsedNs) * 100
        if pct < 0 || pct.isNaN { return 0 }
        return min(100, pct)
    }

    static func cpuPercent(user: UInt64, system: UInt64, idle: UInt64, nice: UInt64) -> Double {
        let total = user + system + idle + nice
        if total == 0 { return 0 }
        let pct = Double(user + system + nice) / Double(total) * 100
        if pct.isNaN || pct < 0 { return 0 }
        return min(100, pct)
    }
}

struct Snapshot {
    var cpu: Double = -1
    var mem: Double = -1
    var disk: Double = -1
    var gpu: Double = -1
    var memUsed: UInt64 = 0
    var memTotal: UInt64 = 0
    var up: Double = -1
    var down: Double = -1
    var wifiConnected = false
    var wifiName = ""
    var wifiLinkMbps: Double = 0
}

final class Settings {
    static let minFontScale: CGFloat = 0.6
    static let maxFontScale: CGFloat = 2.5

    var x: Int?
    var y: Int?
    var width: Int = 0
    var height: Int = 0
    var topMost = true
    var bits = true
    var opacity = 0.9
    var fontScale: CGFloat = 1
    var showClock = false
    var clockAnalog = false
    var showPomodoro = false
    var pomodoroWorkMin = 25
    var pomodoroBreakMin = 5
    var showGmail = false
    var gmailCount = 5
    var gmailIntervalSec = 60

    var anySidePanel: Bool { showClock || showPomodoro || showGmail }

    static func supportDirectory() -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("SysmonWidget", isDirectory: true)
    }

    static func fileURL() -> URL {
        supportDirectory().appendingPathComponent("settings.ini")
    }

    static func load(from url: URL = fileURL()) -> Settings {
        let s = Settings()
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return s }
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces)
            let value = line[line.index(after: eq)...].trimmingCharacters(in: .whitespaces)
            switch key {
            case "X": s.x = Int(value)
            case "Y": s.y = Int(value)
            case "Width": if let n = Int(value), n > 0 { s.width = n }
            case "Height": if let n = Int(value), n > 0 { s.height = n }
            case "TopMost": if let b = parseBool(String(value)) { s.topMost = b }
            case "Bits": if let b = parseBool(String(value)) { s.bits = b }
            case "Opacity":
                if let d = Double(value) { s.opacity = min(1, max(0.3, d)) }
            case "FontScale":
                if let d = Double(value) {
                    s.fontScale = min(maxFontScale, max(minFontScale, CGFloat(d)))
                }
            case "ShowClock": if let b = parseBool(String(value)) { s.showClock = b }
            case "ClockAnalog": if let b = parseBool(String(value)) { s.clockAnalog = b }
            case "ShowPomodoro": if let b = parseBool(String(value)) { s.showPomodoro = b }
            case "PomodoroWorkMin":
                if let n = Int(value), (1...120).contains(n) { s.pomodoroWorkMin = n }
            case "PomodoroBreakMin":
                if let n = Int(value), (1...60).contains(n) { s.pomodoroBreakMin = n }
            case "ShowGmail": if let b = parseBool(String(value)) { s.showGmail = b }
            case "GmailCount":
                if let n = Int(value), (3...10).contains(n) { s.gmailCount = n }
            case "GmailIntervalSec":
                if let n = Int(value), (15...3600).contains(n) { s.gmailIntervalSec = n }
            case "GmailClientId", "GmailClientSecret":
                break // 旧平文キーは migratePlainGmailCredentials で Keychain へ
            default: break
            }
        }
        if migratePlainGmailCredentials(from: url) {
            s.save(to: url)
        }
        return s
    }

    /// 旧 settings.ini の Client ID / Secret を Keychain へ移す。再保存が必要なら true。
    private static func migratePlainGmailCredentials(from url: URL) -> Bool {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return false }
        var id: String?
        var secret: String?
        var hadPlain = false
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let eq = line.firstIndex(of: "=") else { continue }
            let key = line[..<eq].trimmingCharacters(in: .whitespaces)
            let value = String(line[line.index(after: eq)...]).trimmingCharacters(in: .whitespaces)
            if key == "GmailClientId" {
                hadPlain = true
                if !value.isEmpty { id = value }
            } else if key == "GmailClientSecret" {
                hadPlain = true
                secret = value
            }
        }
        guard hadPlain else { return false }
        let existing = GmailAppCredentials.load()
        if existing.clientId.isEmpty, let id, !id.isEmpty {
            GmailAppCredentials.save(clientId: id, clientSecret: secret ?? "")
        }
        return true
    }

    func save(to url: URL = fileURL()) {
        var lines: [String] = []
        if let x { lines.append("X=\(x)") }
        if let y { lines.append("Y=\(y)") }
        lines.append("Width=\(width)")
        lines.append("Height=\(height)")
        lines.append("TopMost=\(topMost)")
        lines.append("Bits=\(bits)")
        lines.append("Opacity=\(String(opacity))")
        lines.append("FontScale=\(String(Double(fontScale)))")
        lines.append("ShowClock=\(showClock)")
        lines.append("ClockAnalog=\(clockAnalog)")
        lines.append("ShowPomodoro=\(showPomodoro)")
        lines.append("PomodoroWorkMin=\(pomodoroWorkMin)")
        lines.append("PomodoroBreakMin=\(pomodoroBreakMin)")
        lines.append("ShowGmail=\(showGmail)")
        lines.append("GmailCount=\(gmailCount)")
        lines.append("GmailIntervalSec=\(gmailIntervalSec)")
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try lines.joined(separator: "\n").appending("\n").write(to: url, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        } catch {}
    }

    private static func parseBool(_ v: String) -> Bool? {
        switch v.lowercased() {
        case "true", "yes", "1": return true
        case "false", "no", "0": return false
        default: return nil
        }
    }
}

enum AutoStart {
    static let label = "com.sysmonwidget.mac"

    static var plistURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }

    static func isEnabled() -> Bool {
        FileManager.default.fileExists(atPath: plistURL.path)
    }

    static func set(enabled: Bool) throws {
        let uid = getuid()
        let service = "gui/\(uid)/\(label)"
        if !enabled {
            _ = launchctl(["bootout", service])
            try? FileManager.default.removeItem(at: plistURL)
            return
        }
        guard let exe = Bundle.main.executablePath else {
            throw Failure("実行ファイルのパスが取れへん")
        }
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [exe],
            "RunAtLoad": true,
            "LimitLoadToSessionType": "Aqua"
        ]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        try FileManager.default.createDirectory(at: plistURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: plistURL, options: .atomic)
        _ = launchctl(["bootout", service])
        if let err = launchctl(["bootstrap", "gui/\(uid)", plistURL.path]) {
            throw Failure(err)
        }
    }

    static func launchAgentPlist(executable: String) throws -> [String: Any] {
        let plist: [String: Any] = [
            "Label": label,
            "ProgramArguments": [executable],
            "RunAtLoad": true,
            "LimitLoadToSessionType": "Aqua"
        ]
        let data = try PropertyListSerialization.data(fromPropertyList: plist, format: .xml, options: 0)
        var format = PropertyListSerialization.PropertyListFormat.xml
        let obj = try PropertyListSerialization.propertyList(from: data, options: [], format: &format)
        return obj as? [String: Any] ?? [:]
    }

    private struct Failure: LocalizedError {
        let message: String
        init(_ message: String) { self.message = message }
        var errorDescription: String? { message }
    }

    @discardableResult
    private static func launchctl(_ args: [String]) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = args
        let err = Pipe()
        process.standardOutput = Pipe()
        process.standardError = err
        do { try process.run() } catch { return error.localizedDescription }
        process.waitUntilExit()
        if process.terminationStatus == 0 { return nil }
        let text = String(data: err.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        if text.isEmpty { return "launchctl \(args.joined(separator: " ")) が失敗した（\(process.terminationStatus)）" }
        return text
    }
}

final class MetricsCollector {
    private struct Ticks {
        var user, system, idle, nice: UInt32
    }
    private struct DiskSample {
        var read: UInt64
        var write: UInt64
    }

    private var prevCPU: Ticks?
    private var prevDisk: [UInt64: DiskSample] = [:]
    private var prevDiskTime: UInt64 = 0
    private var diskFailures = 0
    private var gpuFailures = 0
    private var diskDisabled = false
    private var gpuDisabled = false

    private var wifiName: String?
    private var wifiConnected = false
    private var wifiRate: Double = 0
    private var wifiRefresh = 0
    private var prevWifiID: String?
    private var prevSent: UInt64 = 0
    private var prevRecv: UInt64 = 0
    private var prevNetTime: UInt64 = 0

    func sample() -> Snapshot {
        var s = Snapshot()
        let now = clock_gettime_nsec_np(CLOCK_UPTIME_RAW)
        if let cpu = sampleCPU() { s.cpu = cpu }
        if let mem = sampleMemory() {
            s.mem = mem.percent
            s.memUsed = mem.used
            s.memTotal = mem.total
        }
        if !diskDisabled {
            switch sampleDisk(now: now) {
            case .value(let v):
                s.disk = v
                diskFailures = 0
            case .failed:
                diskFailures += 1
                if diskFailures >= 5 { diskDisabled = true }
            }
        }
        if !gpuDisabled {
            switch sampleGPU() {
            case .value(let v):
                s.gpu = v
                gpuFailures = 0
            case .failed:
                gpuFailures += 1
                if gpuFailures >= 5 { gpuDisabled = true }
            }
        }
        sampleNet(&s, now: now)
        return s
    }

    func gpuDebugSummary() -> String {
        var lines: [String] = []
        _ = eachService("IOAccelerator") { service in
            if lines.count >= 8 { return }
            let klass = registryString(service, "IOClass") ?? "?"
            let dict = registryDict(service, "PerformanceStatistics")
            let keys = dict?.allKeys.compactMap { $0 as? String } ?? []
            let interesting = keys.filter {
                $0.contains("%") || $0.localizedCaseInsensitiveContains("util") || $0.localizedCaseInsensitiveContains("activity")
            }.sorted()
            lines.append("\(klass): \(interesting.joined(separator: ", "))")
        }
        return lines.isEmpty ? "(IOAccelerator なし)" : lines.joined(separator: "\n")
    }

    func interfaceDebugSummary() -> String {
        let map = readInterfaceMap()
        if map.isEmpty { return "(interface なし)" }
        return map.keys.sorted().joined(separator: ", ")
    }

    private enum Reading {
        case value(Double)
        case failed
    }

    private func sampleCPU() -> Double? {
        guard let cur = readCPUTicks() else { return nil }
        defer { prevCPU = cur }
        guard let prev = prevCPU else { return -1 }
        return Format.cpuPercent(
            user: delta32(cur.user, prev.user),
            system: delta32(cur.system, prev.system),
            idle: delta32(cur.idle, prev.idle),
            nice: delta32(cur.nice, prev.nice))
    }

    private func sampleMemory() -> (used: UInt64, total: UInt64, percent: Double)? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64_data_t>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        if kr != KERN_SUCCESS { return nil }
        var page: vm_size_t = 0
        if host_page_size(mach_host_self(), &page) != KERN_SUCCESS || page == 0 { return nil }
        var total: UInt64 = 0
        var size = size_t(MemoryLayout<UInt64>.size)
        if sysctlbyname("hw.memsize", &total, &size, nil, 0) != 0 || total == 0 { return nil }
        let internalPages = UInt64(stats.internal_page_count)
        let purgeable = UInt64(stats.purgeable_count)
        let app = internalPages > purgeable ? internalPages - purgeable : 0
        let usedPages = app + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)
        let used = min(total, usedPages * UInt64(page))
        let pct = min(100, max(0, Double(used) / Double(total) * 100))
        return (used, total, pct)
    }

    private func sampleDisk(now: UInt64) -> Reading {
        var next: [UInt64: DiskSample] = [:]
        var maxBusy = 0.0
        var compared = false
        let opened = eachService("IOBlockStorageDriver") { service in
            var entryID: UInt64 = 0
            guard IORegistryEntryGetRegistryEntryID(service, &entryID) == KERN_SUCCESS,
                  let dict = registryDict(service, "Statistics"),
                  let read = (dict.object(forKey: "Total Time (Read)") as? NSNumber)?.uint64Value,
                  let write = (dict.object(forKey: "Total Time (Write)") as? NSNumber)?.uint64Value
            else { return }
            let cur = DiskSample(read: read, write: write)
            next[entryID] = cur
            if let old = prevDisk[entryID], prevDiskTime > 0, now > prevDiskTime {
                let dRead = cur.read >= old.read ? cur.read - old.read : 0
                let dWrite = cur.write >= old.write ? cur.write - old.write : 0
                let busy = Format.busyPercent(serviceNs: dRead + dWrite, elapsedNs: now - prevDiskTime)
                if busy >= 0 {
                    compared = true
                    maxBusy = max(maxBusy, busy)
                }
            }
        }
        let hadBaseline = !prevDisk.isEmpty
        prevDisk = next
        prevDiskTime = now
        if !opened { return .failed }
        if next.isEmpty { return hadBaseline ? .failed : .value(-1) }
        return .value(compared ? maxBusy : -1)
    }

    private func sampleGPU() -> Reading {
        var maxValue = 0.0
        var found = false
        let opened = eachService("IOAccelerator") { service in
            guard let dict = registryDict(service, "PerformanceStatistics"),
                  let value = gpuPercent(dict) else { return }
            found = true
            maxValue = max(maxValue, value)
        }
        if !opened { return .failed }
        return found ? .value(maxValue) : .failed
    }

    /// 集計済みの使用率を優先する。無ければエンジンごとの最大。
    private func gpuPercent(_ dict: NSDictionary) -> Double? {
        if let v = percentNumber(dict.object(forKey: "GPU Activity(%)")) { return v }
        if let v = percentNumber(dict.object(forKey: "Device Utilization %")) { return v }
        let parts = ["Renderer Utilization %", "Tiler Utilization %"].compactMap {
            percentNumber(dict.object(forKey: $0))
        }
        return parts.max()
    }

    private func percentNumber(_ any: Any?) -> Double? {
        guard let n = any as? NSNumber else { return nil }
        let v = n.doubleValue
        if v.isNaN || v < 0 || v > 100 { return nil }
        return v
    }

    private func sampleNet(_ s: inout Snapshot, now: UInt64) {
        if wifiName == nil || wifiRefresh <= 0 {
            refreshWifi()
            wifiRefresh = 5
        } else {
            wifiRefresh -= 1
        }
        guard let name = wifiName else { return }
        s.wifiConnected = wifiConnected
        s.wifiName = name
        s.wifiLinkMbps = wifiRate
        if !wifiConnected {
            prevWifiID = nil
            return
        }
        guard let counters = readInterfaceMap()[name] else { return }
        if prevWifiID == name, prevNetTime > 0, now > prevNetTime {
            let dt = Double(now - prevNetTime) / 1_000_000_000
            if dt > 0 {
                s.up = Double(delta64(counters.tx, prevSent)) / dt
                s.down = Double(delta64(counters.rx, prevRecv)) / dt
            }
        }
        prevWifiID = name
        prevSent = counters.tx
        prevRecv = counters.rx
        prevNetTime = now
    }

    private func refreshWifi() {
        let client = CWWiFiClient.shared()
        guard let iface = client.interface(),
              let name = iface.interfaceName,
              !name.isEmpty else {
            wifiName = nil
            wifiConnected = false
            wifiRate = 0
            return
        }
        let mode = iface.interfaceMode()
        let rate = iface.transmitRate()
        wifiName = name
        wifiRate = rate
        wifiConnected = rate > 0 || mode == .station || mode == .hostAP || mode == .IBSS
    }

    private func readCPUTicks() -> Ticks? {
        var info = host_cpu_load_info()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info_data_t>.stride / MemoryLayout<integer_t>.stride)
        let kr = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        if kr != KERN_SUCCESS { return nil }
        // CPU_STATE_USER, SYSTEM, IDLE, NICE
        return Ticks(user: info.cpu_ticks.0, system: info.cpu_ticks.1, idle: info.cpu_ticks.2, nice: info.cpu_ticks.3)
    }

    private func delta32(_ cur: UInt32, _ prev: UInt32) -> UInt64 {
        if cur >= prev { return UInt64(cur - prev) }
        return UInt64(UInt32.max - prev) + UInt64(cur) + 1
    }

    private func delta64(_ cur: UInt64, _ prev: UInt64) -> UInt64 {
        cur >= prev ? cur - prev : 0
    }
}

private struct IfaceBytes {
    var rx: UInt64
    var tx: UInt64
}

private func readInterfaceMap() -> [String: IfaceBytes] {
    let sysctlMap = readInterfaceMapSysctl()
    if !sysctlMap.isEmpty { return sysctlMap }
    return readInterfaceMapGetifaddrs()
}

private func readInterfaceMapSysctl() -> [String: IfaceBytes] {
    var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0]
    var length = 0
    if sysctl(&mib, 6, nil, &length, nil, 0) != 0 || length <= 0 { return [:] }
    length += 512
    let raw = UnsafeMutablePointer<UInt8>.allocate(capacity: length)
    defer { raw.deallocate() }
    let rc = sysctl(&mib, 6, raw, &length, nil, 0)
    if rc != 0 || length <= 0 { return [:] }

    let bytes = UnsafeRawPointer(raw)
    var result: [String: IfaceBytes] = [:]
    var offset = 0
    let headerSize = MemoryLayout<if_msghdr2>.stride
    while offset + 4 <= length {
        let msgLen = Int(bytes.loadUnaligned(fromByteOffset: offset, as: UInt16.self))
        // アドレス通知などは if_msghdr2 より短い。短いからといって打ち切らない。
        if msgLen < 4 || offset + msgLen > length { break }
        let type = bytes.loadUnaligned(fromByteOffset: offset + 3, as: UInt8.self)
        if type == UInt8(RTM_IFINFO2), msgLen >= headerSize {
            let header = bytes.loadUnaligned(fromByteOffset: offset, as: if_msghdr2.self)
            let sdl = offset + headerSize
            if sdl + 8 <= length {
                let nlen = Int(bytes.loadUnaligned(fromByteOffset: sdl + 5, as: UInt8.self))
                let nameStart = sdl + 8
                if nlen > 0, nameStart + nlen <= length {
                    let chars = UnsafeBufferPointer(start: raw.advanced(by: nameStart), count: nlen)
                    let name = String(bytes: chars, encoding: .utf8) ?? ""
                    if !name.isEmpty {
                        result[name] = IfaceBytes(rx: header.ifm_data.ifi_ibytes, tx: header.ifm_data.ifi_obytes)
                    }
                }
            }
        }
        offset += msgLen
    }
    return result
}

private func readInterfaceMapGetifaddrs() -> [String: IfaceBytes] {
    var head: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&head) == 0, let first = head else { return [:] }
    defer { freeifaddrs(head) }
    var result: [String: IfaceBytes] = [:]
    var cursor: UnsafeMutablePointer<ifaddrs>? = first
    while let cur = cursor {
        defer { cursor = cur.pointee.ifa_next }
        guard let addr = cur.pointee.ifa_addr,
              addr.pointee.sa_family == sa_family_t(AF_LINK),
              let raw = cur.pointee.ifa_data else { continue }
        let data = raw.assumingMemoryBound(to: if_data.self)
        let name = String(cString: cur.pointee.ifa_name)
        result[name] = IfaceBytes(rx: UInt64(data.pointee.ifi_ibytes), tx: UInt64(data.pointee.ifi_obytes))
    }
    return result
}

@discardableResult
private func eachService(_ name: String, _ body: (io_registry_entry_t) -> Void) -> Bool {
    var iterator: io_iterator_t = 0
    let matching = IOServiceMatching(name)
    guard matching != nil else { return false }
    let kr = IOServiceGetMatchingServices(kIOMainPortDefault, matching, &iterator)
    guard kr == KERN_SUCCESS else { return false }
    defer { IOObjectRelease(iterator) }
    while true {
        let service = IOIteratorNext(iterator)
        if service == 0 { break }
        body(service)
        IOObjectRelease(service)
    }
    return true
}

private func registryDict(_ entry: io_registry_entry_t, _ key: String) -> NSDictionary? {
    guard let unmanaged = IORegistryEntryCreateCFProperty(entry, key as CFString, kCFAllocatorDefault, 0) else {
        return nil
    }
    return unmanaged.takeRetainedValue() as? NSDictionary
}

private func registryString(_ entry: io_registry_entry_t, _ key: String) -> String? {
    guard let unmanaged = IORegistryEntryCreateCFProperty(entry, key as CFString, kCFAllocatorDefault, 0) else {
        return nil
    }
    return unmanaged.takeRetainedValue() as? String
}

private struct Edges: OptionSet {
    let rawValue: Int
    static let left = Edges(rawValue: 1 << 0)
    static let right = Edges(rawValue: 1 << 1)
    static let top = Edges(rawValue: 1 << 2)
    static let bottom = Edges(rawValue: 1 << 3)
}

final class WidgetView: NSView {
    let settings: Settings
    var snapshot: Snapshot?
    var upHist: [Double] = []
    var downHist: [Double] = []
    var gmailItems: [GmailMailItem] = []
    var gmailStatus = ""
    var gmailAuthorized = false
    var onBoundsChanged: (() -> Void)?
    var onStepFont: ((Int) -> Void)?
    var contextMenu: NSMenu?

    // ポモドーロ: 0=停止 1=作業 2=休憩
    var pomoPhase = 0
    var pomoPaused = false
    var pomoDeadline = Date()
    var pomoRemainSec = 25 * 60
    private var pomoToggleRect = NSRect.zero

    private var labelFont = NSFont.systemFont(ofSize: 9)
    private var valueFont = NSFont.systemFont(ofSize: 9.5, weight: .semibold)
    private var smallFont = NSFont.systemFont(ofSize: 8)
    private var clockFont = NSFont.systemFont(ofSize: 22, weight: .semibold)
    private var pomoFont = NSFont.systemFont(ofSize: 18, weight: .semibold)
    private var mailTitleFont = NSFont.systemFont(ofSize: 9.5, weight: .semibold)
    private var mailFromFont = NSFont.systemFont(ofSize: 7.5)
    private var wheelAccum: CGFloat = 0

    init(settings: Settings) {
        self.settings = settings
        super.init(frame: .zero)
        rebuildFonts()
        pomoRemainSec = settings.pomodoroWorkMin * 60
    }

    required init?(coder: NSCoder) { nil }

    override var isFlipped: Bool { true }
    override var isOpaque: Bool { false }

    func rebuildFonts() {
        let k = settings.fontScale
        labelFont = NSFont.systemFont(ofSize: 9 * k)
        valueFont = NSFont.systemFont(ofSize: 9.5 * k, weight: .semibold)
        smallFont = NSFont.systemFont(ofSize: 8 * k)
        clockFont = NSFont.systemFont(ofSize: 22 * k, weight: .semibold)
        pomoFont = NSFont.systemFont(ofSize: 18 * k, weight: .semibold)
        mailTitleFont = NSFont.systemFont(ofSize: 9.5 * k, weight: .semibold)
        mailFromFont = NSFont.systemFont(ofSize: 7.5 * k)
    }

    func S(_ v: CGFloat) -> CGFloat { (v * settings.fontScale).rounded() }

    func minContentSize() -> NSSize {
        var w = Layout.minWidth
        if settings.anySidePanel { w += Layout.panelGap + Layout.rightColMin }
        let h: CGFloat
        if settings.anySidePanel {
            h = max(Layout.metricsContentHeight(Layout.minGraphH), Layout.sidePanelsMinHeight(settings: settings))
        } else {
            h = Layout.metricsContentHeight(Layout.minGraphH)
        }
        return NSSize(width: S(w), height: S(h))
    }

    func preferredSize() -> NSSize {
        var w = Layout.baseWidth
        if settings.anySidePanel { w += Layout.panelGap + Layout.rightColMin }
        let metrics = Layout.metricsContentHeight(Layout.graphH)
        let side = settings.anySidePanel ? Layout.sidePanelsMinHeight(settings: settings) : metrics
        return NSSize(width: S(w), height: S(max(metrics, side)))
    }

    func defaultSize() -> NSSize { preferredSize() }

    func leftColWidth() -> CGFloat {
        if !settings.anySidePanel { return bounds.width }
        let gap = S(Layout.panelGap)
        let minRight = S(Layout.rightColMin)
        let maxLeft = max(S(Layout.minWidth), bounds.width - gap - minRight)
        return max(S(Layout.minWidth), min(S(Layout.baseWidth), maxLeft))
    }

    func push(_ s: Snapshot) {
        snapshot = s
        append(&upHist, s.up)
        append(&downHist, s.down)
        updatePomodoroRemain()
        needsDisplay = true
    }

    func updatePomodoroRemain() {
        if pomoPhase == 0 {
            pomoRemainSec = settings.pomodoroWorkMin * 60
            return
        }
        if pomoPaused { return }
        let left = Int(ceil(pomoDeadline.timeIntervalSinceNow))
        if left <= 0 {
            if pomoPhase == 1 {
                pomoPhase = 2
                pomoDeadline = Date().addingTimeInterval(TimeInterval(settings.pomodoroBreakMin * 60))
                pomoRemainSec = settings.pomodoroBreakMin * 60
            } else {
                pomoPhase = 1
                pomoDeadline = Date().addingTimeInterval(TimeInterval(settings.pomodoroWorkMin * 60))
                pomoRemainSec = settings.pomodoroWorkMin * 60
            }
        } else {
            pomoRemainSec = left
        }
    }

    func togglePomodoroRun() {
        if !settings.showPomodoro { return }
        if pomoPhase == 0 {
            pomoPhase = 1
            pomoPaused = false
            pomoDeadline = Date().addingTimeInterval(TimeInterval(settings.pomodoroWorkMin * 60))
            pomoRemainSec = settings.pomodoroWorkMin * 60
        } else if pomoPaused {
            pomoPaused = false
            pomoDeadline = Date().addingTimeInterval(TimeInterval(max(1, pomoRemainSec)))
        } else {
            updatePomodoroRemain()
            pomoPaused = true
        }
        needsDisplay = true
    }

    func resetPomodoro() {
        pomoPhase = 0
        pomoPaused = false
        pomoRemainSec = settings.pomodoroWorkMin * 60
        needsDisplay = true
    }

    func restartPomodoroPhase() {
        let mins = pomoPhase == 2 ? settings.pomodoroBreakMin : settings.pomodoroWorkMin
        pomoDeadline = Date().addingTimeInterval(TimeInterval(mins * 60))
        pomoRemainSec = mins * 60
        pomoPaused = false
        needsDisplay = true
    }

    private func append(_ list: inout [Double], _ value: Double) {
        list.append(max(0, value))
        if list.count > Layout.histLen { list.removeFirst(list.count - Layout.histLen) }
    }

    override func draw(_ dirtyRect: NSRect) {
        let radius = min(S(14), bounds.width / 2, bounds.height / 2)
        NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius).addClip()
        Palette.bg.setFill()
        bounds.fill()

        let leftW = leftColWidth()
        drawMetricsColumn(colW: leftW)
        if settings.anySidePanel {
            let sx = leftW + S(Layout.panelGap)
            Palette.sep.setStroke()
            let line = NSBezierPath()
            let mid = leftW + S(Layout.panelGap) / 2
            line.move(to: NSPoint(x: mid, y: S(Layout.pad)))
            line.line(to: NSPoint(x: mid, y: bounds.height - S(Layout.pad)))
            line.lineWidth = 1
            line.stroke()
            drawSideColumn(x: sx, w: max(0, bounds.width - sx))
        }
        drawGrip()
    }

    private func drawMetricsColumn(colW: CGFloat) {
        let s = snapshot ?? Snapshot()
        var y = S(Layout.pad)
        let left = S(Layout.pad)
        let right = colW - S(Layout.pad)
        let memDetail: String? = s.memTotal > 0
            ? String(format: "%.1f / %.1f GB", Double(s.memUsed) / 1073741824, Double(s.memTotal) / 1073741824)
            : nil
        drawMetric(left: left, right: right, "CPU", s.cpu, nil, Palette.cpu, &y)
        drawMetric(left: left, right: right, "MEM", s.mem, memDetail, Palette.mem, &y)
        drawMetric(left: left, right: right, "DISK", s.disk, nil, Palette.disk, &y)
        drawMetric(left: left, right: right, "GPU", s.gpu, nil, Palette.gpu, &y)

        let sepY = y + S(Layout.sepH) / 2 - S(Layout.rowGap) / 2
        Palette.sep.setStroke()
        let line = NSBezierPath()
        line.move(to: NSPoint(x: left, y: sepY))
        line.line(to: NSPoint(x: right, y: sepY))
        line.lineWidth = 1
        line.stroke()
        y += S(Layout.sepH) - S(Layout.rowGap) / 2

        drawString("Wi-Fi", font: labelFont, color: Palette.label, x: left, y: y)
        let status: String
        if snapshot == nil {
            status = ""
        } else if !s.wifiConnected {
            status = "未接続"
        } else if s.wifiLinkMbps > 0 {
            status = String(format: "リンク %.0f Mbps", s.wifiLinkMbps)
        } else {
            status = ""
        }
        drawString(status, font: smallFont, color: Palette.dim, x: 0, y: y + S(2), right: right)
        y += S(Layout.netHeaderH)

        let half = max(0, (right - left) / 2)
        drawString("↓ " + Format.speed(s.down, bits: settings.bits), font: valueFont, color: Palette.down, x: left, y: y)
        drawString("↑ " + Format.speed(s.up, bits: settings.bits), font: valueFont, color: Palette.up, x: left + half, y: y)
        y += S(Layout.netSpeedH)

        let graphH = bounds.height - S(Layout.pad) - S(4) - y
        if graphH > 2, right > left {
            drawGraph(NSRect(x: left, y: y, width: max(1, right - left), height: graphH))
        }
    }

    private func drawSideColumn(x: CGFloat, w: CGFloat) {
        var y = S(Layout.pad)
        let left = x
        let innerW = max(0, w - S(Layout.pad))
        pomoToggleRect = .zero

        if settings.showClock {
            let ch = S(settings.clockAnalog ? Layout.clockAnalogH : Layout.clockDigitalH)
            drawClock(NSRect(x: left, y: y, width: innerW, height: ch))
            y += ch + S(8)
        }
        if settings.showPomodoro {
            let ph = S(Layout.pomoH)
            drawPomodoro(NSRect(x: left, y: y, width: innerW, height: ph))
            y += ph + S(8)
        }
        if settings.showGmail {
            let gh = bounds.height - S(Layout.pad) - y
            if gh > S(Layout.gmailHeaderH) {
                drawGmail(NSRect(x: left, y: y, width: innerW, height: gh))
            }
        }
    }

    private func drawClock(_ r: NSRect) {
        let now = Date()
        drawString("時計", font: smallFont, color: Palette.dim, x: r.minX, y: r.minY)
        if settings.clockAnalog {
            let size = min(r.width - S(8), r.height - S(18))
            guard size >= 8 else { return }
            let face = NSRect(x: r.minX + (r.width - size) / 2, y: r.minY + S(16), width: size, height: size)
            Palette.grip.setStroke()
            let circle = NSBezierPath(ovalIn: face)
            circle.lineWidth = 1
            circle.stroke()
            let cx = face.midX, cy = face.midY, rad = face.width / 2
            let cal = Calendar.current
            let h = cal.component(.hour, from: now) % 12
            let m = cal.component(.minute, from: now)
            let s = cal.component(.second, from: now)
            drawHand(cx: cx, cy: cy, length: rad * 0.55, deg: CGFloat(h) * 30 + CGFloat(m) * 0.5, width: 2.5, color: Palette.value)
            drawHand(cx: cx, cy: cy, length: rad * 0.75, deg: CGFloat(m) * 6 + CGFloat(s) * 0.1, width: 2, color: Palette.value)
            drawHand(cx: cx, cy: cy, length: rad * 0.85, deg: CGFloat(s) * 6, width: 1.2, color: Palette.high)
        } else {
            let formatter = DateFormatter()
            formatter.locale = Locale(identifier: "ja_JP")
            formatter.dateFormat = "HH:mm:ss"
            let time = formatter.string(from: now)
            formatter.dateFormat = "yyyy/MM/dd EEE"
            let date = formatter.string(from: now)
            let tw = (time as NSString).size(withAttributes: [.font: clockFont]).width
            drawString(time, font: clockFont, color: Palette.value, x: r.minX + max(0, (r.width - tw) / 2), y: r.minY + S(14))
            let dw = (date as NSString).size(withAttributes: [.font: smallFont]).width
            drawString(date, font: smallFont, color: Palette.dim, x: r.minX + max(0, (r.width - dw) / 2), y: r.minY + S(40))
        }
    }

    private func drawHand(cx: CGFloat, cy: CGFloat, length: CGFloat, deg: CGFloat, width: CGFloat, color: NSColor) {
        let rad = (deg - 90) * .pi / 180
        let tip = NSPoint(x: cx + cos(rad) * length, y: cy + sin(rad) * length)
        let path = NSBezierPath()
        path.lineWidth = width
        path.lineCapStyle = .round
        path.move(to: NSPoint(x: cx, y: cy))
        path.line(to: tip)
        color.setStroke()
        path.stroke()
    }

    private func drawPomodoro(_ r: NSRect) {
        updatePomodoroRemain()
        let phase: String
        switch pomoPhase {
        case 1: phase = pomoPaused ? "作業 一時停止" : "作業中"
        case 2: phase = pomoPaused ? "休憩 一時停止" : "休憩中"
        default: phase = "停止"
        }
        drawString("ポモドーロ · \(phase)", font: smallFont, color: Palette.dim, x: r.minX, y: r.minY)
        let mmss = String(format: "%02d:%02d", max(0, pomoRemainSec) / 60, max(0, pomoRemainSec) % 60)
        let tw = (mmss as NSString).size(withAttributes: [.font: pomoFont]).width
        drawString(mmss, font: pomoFont, color: Palette.value, x: r.minX + max(0, (r.width - tw) / 2), y: r.minY + S(16))

        let btn = pomoPhase == 0 ? "開始" : (pomoPaused ? "再開" : "一時停止")
        let bs = (btn as NSString).size(withAttributes: [.font: smallFont])
        let bw = bs.width + S(16), bh = S(20)
        pomoToggleRect = NSRect(x: r.minX + (r.width - bw) / 2, y: r.maxY - bh - S(2), width: bw, height: bh)
        fillRound(pomoToggleRect, Palette.track)
        drawString(btn, font: smallFont, color: Palette.value,
                   x: pomoToggleRect.minX + (pomoToggleRect.width - bs.width) / 2,
                   y: pomoToggleRect.minY + (pomoToggleRect.height - bs.height) / 2)
    }

    private func drawGmail(_ r: NSRect) {
        drawString("Gmail", font: smallFont, color: Palette.dim, x: r.minX, y: r.minY)
        if !gmailStatus.isEmpty {
            let tip = ellipsize(gmailStatus, font: smallFont, maxWidth: r.width / 2)
            drawString(tip, font: smallFont, color: Palette.dim, x: 0, y: r.minY, right: r.maxX)
        }
        var y = r.minY + S(Layout.gmailHeaderH)
        let rowH = S(Layout.gmailRowH)
        var shown = 0
        for m in gmailItems {
            guard y + rowH <= r.maxY else { break }
            let from = ellipsize(m.from, font: mailFromFont, maxWidth: r.width)
            let sub = ellipsize(m.subject, font: mailTitleFont, maxWidth: r.width)
            drawString(from, font: mailFromFont, color: Palette.dim, x: r.minX, y: y)
            drawString(sub, font: mailTitleFont, color: Palette.value, x: r.minX, y: y + S(12))
            y += rowH
            shown += 1
        }
        if shown == 0 {
            let tip = gmailAuthorized ? "メールなし / 取得待ち" : "メニューから接続"
            drawString(tip, font: smallFont, color: Palette.dim, x: r.minX, y: y)
        }
    }

    private func ellipsize(_ text: String, font: NSFont, maxWidth: CGFloat) -> String {
        if text.isEmpty || maxWidth <= 0 { return "" }
        let attrs: [NSAttributedString.Key: Any] = [.font: font]
        if (text as NSString).size(withAttributes: attrs).width <= maxWidth { return text }
        let ell = "…"
        var lo = 0, hi = text.count
        while lo < hi {
            let mid = (lo + hi + 1) / 2
            let idx = text.index(text.startIndex, offsetBy: mid)
            let candidate = String(text[..<idx]) + ell
            if (candidate as NSString).size(withAttributes: attrs).width <= maxWidth { lo = mid }
            else { hi = mid - 1 }
        }
        if lo <= 0 { return ell }
        let idx = text.index(text.startIndex, offsetBy: lo)
        return String(text[..<idx]) + ell
    }

    private func drawMetric(left: CGFloat, right: CGFloat, _ label: String, _ value: Double, _ detail: String?, _ accent: NSColor, _ y: inout CGFloat) {
        drawString(label, font: labelFont, color: Palette.label, x: left, y: y)
        let valueWidth = drawString(Format.percent(value), font: valueFont, color: Palette.value, x: 0, y: y - S(1), right: right)
        if let detail {
            drawString(detail, font: smallFont, color: Palette.dim, x: 0, y: y + S(2), right: right - valueWidth - S(4))
        }
        y += S(Layout.labelH)

        let track = NSRect(x: left, y: y, width: max(0, right - left), height: max(1, S(Layout.barH)))
        fillRound(track, Palette.track)
        if value > 0, track.width > 0 {
            let shown = min(100, value)
            var fillW = track.width * CGFloat(shown) / 100
            fillW = min(track.width, max(track.height, fillW))
            let color = shown >= 90 ? Palette.high : accent
            fillRound(NSRect(x: track.minX, y: track.minY, width: fillW, height: track.height), color)
        }
        y += S(Layout.barH) + S(Layout.rowGap)
    }

    @discardableResult
    private func drawString(_ text: String, font: NSFont, color: NSColor, x: CGFloat, y: CGFloat, right: CGFloat? = nil) -> CGFloat {
        if text.isEmpty { return 0 }
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
        let size = (text as NSString).size(withAttributes: attrs)
        let drawX = right.map { $0 - size.width } ?? x
        let rect = NSRect(x: drawX, y: y, width: ceil(size.width) + 2, height: ceil(size.height) + 2)
        (text as NSString).draw(with: rect, options: [.usesLineFragmentOrigin], attributes: attrs)
        return size.width
    }

    private func fillRound(_ rect: NSRect, _ color: NSColor) {
        let d = min(rect.width, rect.height)
        color.setFill()
        if d <= 1 {
            NSBezierPath(rect: rect).fill()
            return
        }
        NSBezierPath(roundedRect: rect, xRadius: d / 2, yRadius: d / 2).fill()
    }

    private func drawGraph(_ rect: NSRect) {
        Palette.graphBg.setFill()
        NSBezierPath(rect: rect).fill()
        guard downHist.count >= 2, upHist.count >= 2 else { return }

        var maxValue = 1024.0
        for v in downHist { maxValue = max(maxValue, v) }
        for v in upHist { maxValue = max(maxValue, v) }
        let step = rect.width / CGFloat(Layout.histLen - 1)

        func points(_ list: [Double]) -> [NSPoint] {
            let offset = Layout.histLen - list.count
            return list.enumerated().map { i, value in
                let x = rect.minX + CGFloat(offset + i) * step
                let y = rect.maxY - 1 - CGFloat(value / maxValue) * (rect.height - 2)
                return NSPoint(x: x, y: y)
            }
        }

        let down = points(downHist)
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(rect: rect).addClip()
        if let first = down.first, let last = down.last {
            let area = NSBezierPath()
            area.move(to: first)
            for p in down.dropFirst() { area.line(to: p) }
            area.line(to: NSPoint(x: last.x, y: rect.maxY))
            area.line(to: NSPoint(x: first.x, y: rect.maxY))
            area.close()
            Palette.down.withAlphaComponent(70.0 / 255.0).setFill()
            area.fill()
        }
        stroke(down, Palette.down)
        stroke(points(upHist), Palette.up)
        NSGraphicsContext.restoreGraphicsState()
        drawString(Format.speed(maxValue, bits: settings.bits), font: smallFont, color: Palette.dim, x: rect.minX + S(2), y: rect.minY + S(1))
    }

    private func stroke(_ points: [NSPoint], _ color: NSColor) {
        guard let first = points.first else { return }
        let path = NSBezierPath()
        path.lineWidth = 1
        path.lineJoinStyle = .round
        path.move(to: first)
        for p in points.dropFirst() { path.line(to: p) }
        color.setStroke()
        path.stroke()
    }

    private func drawGrip() {
        let d: CGFloat = 1.5
        let gap: CGFloat = 4
        let x0 = bounds.width - 4
        let y0 = bounds.height - 4
        Palette.grip.setFill()
        for i in 0..<3 {
            for j in 0...i {
                let x = x0 - CGFloat(j) * gap - d
                let y = y0 - CGFloat(i - j) * gap - d
                NSBezierPath(ovalIn: NSRect(x: x, y: y, width: d, height: d)).fill()
            }
        }
    }

    override func resetCursorRects() {
        let b = bounds
        let e = Layout.resizeBorder
        addCursorRect(NSRect(x: 0, y: e, width: e, height: max(0, b.height - e * 2)), cursor: .resizeLeftRight)
        addCursorRect(NSRect(x: b.width - e, y: e, width: e, height: max(0, b.height - e * 2)), cursor: .resizeLeftRight)
        addCursorRect(NSRect(x: e, y: 0, width: max(0, b.width - e * 2), height: e), cursor: .resizeUpDown)
        addCursorRect(NSRect(x: e, y: b.height - e, width: max(0, b.width - e * 2), height: e), cursor: .resizeUpDown)
        let corner = NSCursor.crosshair
        addCursorRect(NSRect(x: 0, y: 0, width: e, height: e), cursor: corner)
        addCursorRect(NSRect(x: b.width - e, y: 0, width: e, height: e), cursor: corner)
        addCursorRect(NSRect(x: 0, y: b.height - e, width: e, height: e), cursor: corner)
        addCursorRect(NSRect(x: b.width - e, y: b.height - e, width: e, height: e), cursor: corner)
    }

    override func mouseDown(with event: NSEvent) {
        guard let window else { return }
        let local = convert(event.locationInWindow, from: nil)
        if settings.showPomodoro, pomoToggleRect.contains(local) {
            togglePomodoroRun()
            return
        }
        let edges = hitEdges(local)
        if edges.isEmpty {
            window.performDrag(with: event)
            onBoundsChanged?()
            return
        }
        let startMouse = NSEvent.mouseLocation
        let startFrame = window.frame
        var current: NSEvent? = event
        while let e = current, e.type != .leftMouseUp {
            applyResize(edges, startMouse: startMouse, startFrame: startFrame)
            current = window.nextEvent(matching: [.leftMouseDragged, .leftMouseUp])
        }
        onBoundsChanged?()
    }

    override func rightMouseDown(with event: NSEvent) {
        contextMenu?.popUp(positioning: nil, at: convert(event.locationInWindow, from: nil), in: self)
    }

    override func scrollWheel(with event: NSEvent) {
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        guard flags.contains(.control) || flags.contains(.command) else {
            super.scrollWheel(with: event)
            return
        }
        if event.hasPreciseScrollingDeltas {
            wheelAccum += event.scrollingDeltaY
            if abs(wheelAccum) < 30 { return }
            let dir = wheelAccum > 0 ? 1 : -1
            wheelAccum = 0
            onStepFont?(dir)
        } else if event.scrollingDeltaY != 0 {
            onStepFont?(event.scrollingDeltaY > 0 ? 1 : -1)
        }
    }

    private func hitEdges(_ p: NSPoint) -> Edges {
        let e = Layout.resizeBorder
        var edges: Edges = []
        if p.x < e { edges.insert(.left) }
        if p.x >= bounds.width - e { edges.insert(.right) }
        if p.y < e { edges.insert(.top) }
        if p.y >= bounds.height - e { edges.insert(.bottom) }
        return edges
    }

    private func applyResize(_ edges: Edges, startMouse: NSPoint, startFrame: NSRect) {
        guard let window else { return }
        let mouse = NSEvent.mouseLocation
        let dx = mouse.x - startMouse.x
        let dy = mouse.y - startMouse.y
        var frame = startFrame
        if edges.contains(.left) {
            frame.origin.x += dx
            frame.size.width -= dx
        }
        if edges.contains(.right) {
            frame.size.width += dx
        }
        if edges.contains(.bottom) {
            frame.origin.y += dy
            frame.size.height -= dy
        }
        if edges.contains(.top) {
            frame.size.height += dy
        }
        let min = minContentSize()
        if frame.size.width < min.width {
            if edges.contains(.left) { frame.origin.x -= min.width - frame.size.width }
            frame.size.width = min.width
        }
        if frame.size.height < min.height {
            if edges.contains(.bottom) { frame.origin.y -= min.height - frame.size.height }
            frame.size.height = min.height
        }
        window.minSize = min
        window.setFrame(frame, display: true)
    }
}

final class WidgetWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

enum StatusIcon {
    static func image() -> NSImage {
        let size = NSSize(width: 18, height: 18)
        return NSImage(size: size, flipped: false) { _ in
            func bar(_ x: CGFloat, _ h: CGFloat, _ color: NSColor) {
                color.setFill()
                NSBezierPath(rect: NSRect(x: x, y: 2, width: 3, height: h)).fill()
            }
            bar(3, 6, Palette.cpu)
            bar(8, 10, Palette.mem)
            bar(13, 13, Palette.gpu)
            return true
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let settings = Settings.load()
    private let collector = MetricsCollector()
    private let gmail = GmailClient()
    private var window: WidgetWindow?
    private var widget: WidgetView?
    private var statusItem: NSStatusItem?
    private var timer: Timer?
    private var rootMenu: NSMenu?
    private var opacityMenu: NSMenu?
    private var unitMenu: NSMenu?
    private var fontMenu: NSMenu?
    private var workMenu: NSMenu?
    private var breakMenu: NSMenu?
    private var gmailCountMenu: NSMenu?
    private var gmailIntervalMenu: NSMenu?
    private var topMostItem: NSMenuItem?
    private var autoStartItem: NSMenuItem?
    private var bitsItem: NSMenuItem?
    private var bytesItem: NSMenuItem?
    private var clockShowItem: NSMenuItem?
    private var clockDigitalItem: NSMenuItem?
    private var clockAnalogItem: NSMenuItem?
    private var pomoShowItem: NSMenuItem?
    private var gmailShowItem: NSMenuItem?
    private var suppressStatusAction = false
    private var gmailNextFetch = Date.distantPast
    private var gmailFetching = false
    private var metricsTick = 0

    func applicationDidFinishLaunching(_ notification: Notification) {
        gmail.loadStoredToken()
        let view = WidgetView(settings: settings)
        view.gmailAuthorized = gmail.isAuthorized
        view.onBoundsChanged = { [weak self] in self?.saveBounds() }
        view.onStepFont = { [weak self] dir in self?.stepFont(dir) }

        let frame = initialFrame(for: view)
        let window = WidgetWindow(contentRect: frame, styleMask: [.borderless, .resizable], backing: .buffered, defer: false)
        window.title = "SysmonWidget"
        window.isOpaque = false
        window.backgroundColor = .clear
        window.hasShadow = true
        window.level = settings.topMost ? .floating : .normal
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        window.isMovableByWindowBackground = false
        window.minSize = view.minContentSize()
        window.alphaValue = settings.opacity
        view.frame = window.contentView?.bounds ?? NSRect(origin: .zero, size: frame.size)
        view.autoresizingMask = [.width, .height]
        window.contentView = view

        let menu = buildMenu()
        view.contextMenu = menu
        window.contentView = view

        self.window = window
        self.widget = view
        installStatusItem(menu: menu)
        window.orderFrontRegardless()
        saveBounds()

        let timer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            self?.tick()
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        tick()
        if settings.showGmail { ensureGmailFetch(force: true) }
    }

    func applicationWillTerminate(_ notification: Notification) {
        timer?.invalidate()
        saveBounds()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    private func tick() {
        metricsTick += 1
        if metricsTick >= 4 {
            metricsTick = 0
            let snap = collector.sample()
            widget?.push(snap)
            let tip = "CPU \(Format.percent(snap.cpu))  MEM \(Format.percent(snap.mem))  GPU \(Format.percent(snap.gpu))"
            statusItem?.button?.toolTip = String(tip.prefix(120))
        } else if settings.showClock || settings.showPomodoro {
            widget?.updatePomodoroRemain()
            widget?.needsDisplay = true
        }
        if settings.showGmail { ensureGmailFetch(force: false) }
    }

    private func ensureSideWidth() {
        guard let window, let widget else { return }
        let min = widget.minContentSize()
        var frame = window.frame
        var changed = false
        if frame.width < min.width {
            frame.size.width = min.width
            changed = true
        }
        if frame.height < min.height {
            let top = frame.maxY
            frame.size.height = min.height
            frame.origin.y = top - min.height
            changed = true
        }
        if changed {
            window.minSize = min
            window.setFrame(frame, display: true)
            saveBounds()
        } else {
            window.minSize = min
        }
        widget.needsDisplay = true
    }

    private func installStatusItem(menu: NSMenu) {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            let image = StatusIcon.image()
            image.isTemplate = false
            button.image = image
            button.target = self
            button.action = #selector(statusClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.toolTip = "SysmonWidget"
        }
        statusItem = item
        rootMenu = menu
    }

    @objc private func statusClicked(_ sender: Any?) {
        if suppressStatusAction { return }
        let event = NSApp.currentEvent
        let controlClick = event?.type == .leftMouseUp && event?.modifierFlags.contains(.control) == true
        if event?.type == .rightMouseUp || controlClick {
            guard let button = statusItem?.button, let menu = rootMenu else { return }
            suppressStatusAction = true
            statusItem?.menu = menu
            button.performClick(nil)
            statusItem?.menu = nil
            suppressStatusAction = false
        } else {
            toggleVisible()
        }
    }

    private func toggleVisible() {
        guard let window else { return }
        if window.isVisible {
            window.orderOut(nil)
        } else {
            window.makeKeyAndOrderFront(nil)
            if #available(macOS 14.0, *) {
                NSApp.activate()
            } else {
                NSApp.activate(ignoringOtherApps: true)
            }
        }
    }

    private func initialFrame(for view: WidgetView) -> NSRect {
        let minSize = view.minContentSize()
        let width = max(minSize.width, settings.width > 0 ? CGFloat(settings.width) : view.defaultSize().width)
        let height = max(minSize.height, settings.height > 0 ? CGFloat(settings.height) : view.defaultSize().height)
        let size = NSSize(width: width, height: height)
        if let x = settings.x, let y = settings.y {
            let saved = NSRect(x: CGFloat(x), y: CGFloat(y), width: size.width, height: size.height)
            if NSScreen.screens.contains(where: { $0.frame.intersects(saved) }) {
                return saved
            }
        }
        let vf = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        return NSRect(
            x: vf.maxX - size.width - view.S(16),
            y: vf.maxY - size.height - view.S(16),
            width: size.width,
            height: size.height)
    }

    private func saveBounds() {
        guard let window else { return }
        settings.x = Int(window.frame.origin.x.rounded())
        settings.y = Int(window.frame.origin.y.rounded())
        settings.width = Int(window.frame.width.rounded())
        settings.height = Int(window.frame.height.rounded())
        settings.save()
    }

    private func setFontScale(_ scale: CGFloat) {
        let scale = min(Settings.maxFontScale, max(Settings.minFontScale, scale))
        let old = settings.fontScale
        if abs(scale - old) < 0.001 { return }
        guard let window, let widget else { return }
        let oldExtra = max(0, window.frame.height - widget.preferredSize().height)
        let ratio = scale / old
        let newW = (window.frame.width * ratio).rounded()
        let screen = window.screen ?? NSScreen.main
        let anchorRight = (screen?.visibleFrame.midX ?? 0) < window.frame.midX
        let right = window.frame.maxX
        let top = window.frame.maxY
        settings.fontScale = scale
        widget.rebuildFonts()
        let minSize = widget.minContentSize()
        let preferred = widget.preferredSize()
        let size = NSSize(
            width: max(minSize.width, newW),
            height: max(minSize.height, preferred.height + (oldExtra * ratio).rounded()))
        var origin = NSPoint(x: window.frame.origin.x, y: top - size.height)
        if anchorRight { origin.x = right - size.width }
        window.minSize = minSize
        window.setFrame(NSRect(origin: origin, size: size), display: true)
        window.invalidateCursorRects(for: widget)
        saveBounds()
    }

    private func stepFont(_ dir: Int) {
        let cur = settings.fontScale
        let next: CGFloat?
        if dir > 0 {
            next = Layout.fontScales.first { $0 > cur + 0.001 }
        } else {
            next = Layout.fontScales.last { $0 < cur - 0.001 }
        }
        if let next { setFontScale(next) }
    }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.delegate = self

        let top = NSMenuItem(title: "常に手前に表示", action: #selector(toggleTopMost), keyEquivalent: "")
        top.target = self
        top.state = settings.topMost ? .on : .off
        topMostItem = top

        let opacity = NSMenu(title: "透明度")
        opacity.autoenablesItems = false
        opacity.delegate = self
        for pct in [100, 90, 80, 70, 60, 50] {
            let item = NSMenuItem(title: "\(pct)%", action: #selector(setOpacity(_:)), keyEquivalent: "")
            item.target = self
            item.tag = pct
            opacity.addItem(item)
        }
        opacityMenu = opacity
        let opacityItem = NSMenuItem(title: "透明度", action: nil, keyEquivalent: "")
        opacityItem.submenu = opacity

        let font = NSMenu(title: "文字サイズ")
        font.autoenablesItems = false
        font.delegate = self
        let larger = NSMenuItem(title: "大きく（Ctrl / ⌘ + スクロール↑）", action: #selector(fontLarger), keyEquivalent: "")
        larger.target = self
        let smaller = NSMenuItem(title: "小さく（Ctrl / ⌘ + スクロール↓）", action: #selector(fontSmaller), keyEquivalent: "")
        smaller.target = self
        font.addItem(larger)
        font.addItem(smaller)
        font.addItem(.separator())
        for scale in Layout.fontScales {
            let item = NSMenuItem(title: "\(Int((scale * 100).rounded()))%", action: #selector(setFontPreset(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = NSNumber(value: Double(scale))
            font.addItem(item)
        }
        fontMenu = font
        let fontItem = NSMenuItem(title: "文字サイズ", action: nil, keyEquivalent: "")
        fontItem.submenu = font

        let unit = NSMenu(title: "速度の単位")
        unit.autoenablesItems = false
        unit.delegate = self
        let bits = NSMenuItem(title: "bps（ビット/秒）", action: #selector(setBits(_:)), keyEquivalent: "")
        bits.target = self
        bits.tag = 1
        bits.state = settings.bits ? .on : .off
        let bytes = NSMenuItem(title: "B/s（バイト/秒）", action: #selector(setBytes(_:)), keyEquivalent: "")
        bytes.target = self
        bytes.tag = 0
        bytes.state = settings.bits ? .off : .on
        unit.addItem(bits)
        unit.addItem(bytes)
        bitsItem = bits
        bytesItem = bytes
        unitMenu = unit
        let unitItem = NSMenuItem(title: "速度の単位", action: nil, keyEquivalent: "")
        unitItem.submenu = unit

        let clockMenu = NSMenu(title: "時計")
        clockMenu.autoenablesItems = false
        clockMenu.delegate = self
        let clockShow = NSMenuItem(title: "表示する", action: #selector(toggleClock), keyEquivalent: "")
        clockShow.target = self
        clockShowItem = clockShow
        let clockDigital = NSMenuItem(title: "デジタル", action: #selector(setClockDigital), keyEquivalent: "")
        clockDigital.target = self
        clockDigitalItem = clockDigital
        let clockAnalog = NSMenuItem(title: "アナログ", action: #selector(setClockAnalog), keyEquivalent: "")
        clockAnalog.target = self
        clockAnalogItem = clockAnalog
        clockMenu.addItem(clockShow)
        clockMenu.addItem(.separator())
        clockMenu.addItem(clockDigital)
        clockMenu.addItem(clockAnalog)
        let clockItem = NSMenuItem(title: "時計", action: nil, keyEquivalent: "")
        clockItem.submenu = clockMenu

        let pomoMenu = NSMenu(title: "ポモドーロ")
        pomoMenu.autoenablesItems = false
        pomoMenu.delegate = self
        let pomoShow = NSMenuItem(title: "表示する", action: #selector(togglePomodoroPanel), keyEquivalent: "")
        pomoShow.target = self
        pomoShowItem = pomoShow
        let pomoStart = NSMenuItem(title: "開始 / 一時停止", action: #selector(pomoStartPause), keyEquivalent: "")
        pomoStart.target = self
        let pomoResetItem = NSMenuItem(title: "リセット", action: #selector(pomoReset), keyEquivalent: "")
        pomoResetItem.target = self
        let work = NSMenu(title: "作業時間")
        work.autoenablesItems = false
        work.delegate = self
        for m in Layout.workMins {
            let item = NSMenuItem(title: "\(m) 分", action: #selector(setWorkMin(_:)), keyEquivalent: "")
            item.target = self
            item.tag = m
            work.addItem(item)
        }
        workMenu = work
        let workItem = NSMenuItem(title: "作業時間", action: nil, keyEquivalent: "")
        workItem.submenu = work
        let brk = NSMenu(title: "休憩時間")
        brk.autoenablesItems = false
        brk.delegate = self
        for m in Layout.breakMins {
            let item = NSMenuItem(title: "\(m) 分", action: #selector(setBreakMin(_:)), keyEquivalent: "")
            item.target = self
            item.tag = m
            brk.addItem(item)
        }
        breakMenu = brk
        let breakItem = NSMenuItem(title: "休憩時間", action: nil, keyEquivalent: "")
        breakItem.submenu = brk
        pomoMenu.addItem(pomoShow)
        pomoMenu.addItem(.separator())
        pomoMenu.addItem(pomoStart)
        pomoMenu.addItem(pomoResetItem)
        pomoMenu.addItem(workItem)
        pomoMenu.addItem(breakItem)
        let pomoItem = NSMenuItem(title: "ポモドーロ", action: nil, keyEquivalent: "")
        pomoItem.submenu = pomoMenu

        let gmailMenu = NSMenu(title: "Gmail")
        gmailMenu.autoenablesItems = false
        gmailMenu.delegate = self
        let gmailShow = NSMenuItem(title: "表示する", action: #selector(toggleGmail), keyEquivalent: "")
        gmailShow.target = self
        gmailShowItem = gmailShow
        let gmailConnect = NSMenuItem(title: "接続…", action: #selector(gmailConnect), keyEquivalent: "")
        gmailConnect.target = self
        let gmailDisconnect = NSMenuItem(title: "切断", action: #selector(gmailDisconnect), keyEquivalent: "")
        gmailDisconnect.target = self
        let gmailCred = NSMenuItem(title: "Client ID を設定…", action: #selector(gmailEditCredentials), keyEquivalent: "")
        gmailCred.target = self
        let gmailRefresh = NSMenuItem(title: "今すぐ更新", action: #selector(gmailRefreshNow), keyEquivalent: "")
        gmailRefresh.target = self
        let countMenu = NSMenu(title: "表示件数")
        countMenu.autoenablesItems = false
        countMenu.delegate = self
        for n in 3...10 {
            let item = NSMenuItem(title: "\(n) 件", action: #selector(setGmailCount(_:)), keyEquivalent: "")
            item.target = self
            item.tag = n
            countMenu.addItem(item)
        }
        gmailCountMenu = countMenu
        let countItem = NSMenuItem(title: "表示件数", action: nil, keyEquivalent: "")
        countItem.submenu = countMenu
        let intervalMenu = NSMenu(title: "更新間隔")
        intervalMenu.autoenablesItems = false
        intervalMenu.delegate = self
        for sec in Layout.gmailIntervals {
            let item = NSMenuItem(title: Self.formatGmailInterval(sec), action: #selector(setGmailInterval(_:)), keyEquivalent: "")
            item.target = self
            item.tag = sec
            intervalMenu.addItem(item)
        }
        gmailIntervalMenu = intervalMenu
        let intervalItem = NSMenuItem(title: "更新間隔", action: nil, keyEquivalent: "")
        intervalItem.submenu = intervalMenu
        gmailMenu.addItem(gmailShow)
        gmailMenu.addItem(.separator())
        gmailMenu.addItem(gmailConnect)
        gmailMenu.addItem(gmailDisconnect)
        gmailMenu.addItem(gmailCred)
        gmailMenu.addItem(gmailRefresh)
        gmailMenu.addItem(countItem)
        gmailMenu.addItem(intervalItem)
        let gmailItem = NSMenuItem(title: "Gmail", action: nil, keyEquivalent: "")
        gmailItem.submenu = gmailMenu

        let auto = NSMenuItem(title: "ログイン時に自動起動", action: #selector(toggleAutoStart), keyEquivalent: "")
        auto.target = self
        auto.state = AutoStart.isEnabled() ? .on : .off
        autoStartItem = auto

        let reset = NSMenuItem(title: "位置とサイズをリセット", action: #selector(resetPosition), keyEquivalent: "")
        reset.target = self
        let quit = NSMenuItem(title: "終了", action: #selector(quit), keyEquivalent: "q")
        quit.target = self

        menu.addItem(top)
        menu.addItem(opacityItem)
        menu.addItem(fontItem)
        menu.addItem(unitItem)
        menu.addItem(clockItem)
        menu.addItem(pomoItem)
        menu.addItem(gmailItem)
        menu.addItem(auto)
        menu.addItem(reset)
        menu.addItem(.separator())
        menu.addItem(quit)
        rootMenu = menu
        return menu
    }

    func menuWillOpen(_ menu: NSMenu) {
        if menu === opacityMenu {
            let pct = Int((settings.opacity * 100).rounded())
            for item in menu.items { item.state = item.tag == pct ? .on : .off }
        } else if menu === fontMenu {
            for item in menu.items {
                guard let n = item.representedObject as? NSNumber else { continue }
                item.state = abs(CGFloat(truncating: n) - settings.fontScale) < 0.001 ? .on : .off
            }
        } else if menu === unitMenu {
            bitsItem?.state = settings.bits ? .on : .off
            bytesItem?.state = settings.bits ? .off : .on
        } else if menu === workMenu {
            for item in menu.items { item.state = item.tag == settings.pomodoroWorkMin ? .on : .off }
        } else if menu === breakMenu {
            for item in menu.items { item.state = item.tag == settings.pomodoroBreakMin ? .on : .off }
        } else if menu === gmailCountMenu {
            for item in menu.items { item.state = item.tag == settings.gmailCount ? .on : .off }
        } else if menu === gmailIntervalMenu {
            for item in menu.items { item.state = item.tag == settings.gmailIntervalSec ? .on : .off }
        } else if menu === rootMenu {
            topMostItem?.state = settings.topMost ? .on : .off
            autoStartItem?.state = AutoStart.isEnabled() ? .on : .off
        }
        clockShowItem?.state = settings.showClock ? .on : .off
        clockDigitalItem?.state = settings.clockAnalog ? .off : .on
        clockAnalogItem?.state = settings.clockAnalog ? .on : .off
        pomoShowItem?.state = settings.showPomodoro ? .on : .off
        gmailShowItem?.state = settings.showGmail ? .on : .off
    }

    @objc private func toggleTopMost() {
        settings.topMost.toggle()
        window?.level = settings.topMost ? .floating : .normal
        topMostItem?.state = settings.topMost ? .on : .off
        settings.save()
    }

    @objc private func setOpacity(_ sender: NSMenuItem) {
        let value = min(1, max(0.3, Double(sender.tag) / 100))
        settings.opacity = value
        window?.alphaValue = value
        settings.save()
    }

    @objc private func setBits(_ sender: NSMenuItem) { setUnit(true) }
    @objc private func setBytes(_ sender: NSMenuItem) { setUnit(false) }

    private func setUnit(_ bits: Bool) {
        settings.bits = bits
        bitsItem?.state = bits ? .on : .off
        bytesItem?.state = bits ? .off : .on
        settings.save()
        widget?.needsDisplay = true
    }

    @objc private func toggleAutoStart() {
        do {
            try AutoStart.set(enabled: !AutoStart.isEnabled())
            autoStartItem?.state = AutoStart.isEnabled() ? .on : .off
        } catch {
            let alert = NSAlert()
            alert.messageText = "SysmonWidget"
            alert.informativeText = "自動起動の設定に失敗しました: \(error.localizedDescription)"
            alert.runModal()
        }
    }

    @objc private func fontLarger() { stepFont(1) }
    @objc private func fontSmaller() { stepFont(-1) }

    @objc private func setFontPreset(_ sender: NSMenuItem) {
        guard let n = sender.representedObject as? NSNumber else { return }
        setFontScale(CGFloat(truncating: n))
    }

    @objc private func resetPosition() {
        guard let window, let widget else { return }
        let size = widget.preferredSize()
        let vf = (window.screen ?? NSScreen.main)?.visibleFrame
            ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let frame = NSRect(
            x: vf.maxX - size.width - widget.S(16),
            y: vf.maxY - size.height - widget.S(16),
            width: size.width,
            height: size.height)
        window.minSize = widget.minContentSize()
        window.setFrame(frame, display: true)
        saveBounds()
    }

    @objc private func toggleClock() {
        settings.showClock.toggle()
        settings.save()
        ensureSideWidth()
    }

    @objc private func setClockDigital() {
        settings.clockAnalog = false
        settings.save()
        ensureSideWidth()
    }

    @objc private func setClockAnalog() {
        settings.clockAnalog = true
        settings.save()
        ensureSideWidth()
    }

    @objc private func togglePomodoroPanel() {
        settings.showPomodoro.toggle()
        if !settings.showPomodoro { widget?.resetPomodoro() }
        settings.save()
        ensureSideWidth()
    }

    @objc private func pomoStartPause() { widget?.togglePomodoroRun() }
    @objc private func pomoReset() { widget?.resetPomodoro() }

    @objc private func setWorkMin(_ sender: NSMenuItem) {
        settings.pomodoroWorkMin = sender.tag
        settings.save()
        if widget?.pomoPhase == 1, widget?.pomoPaused == false {
            widget?.restartPomodoroPhase()
        }
    }

    @objc private func setBreakMin(_ sender: NSMenuItem) {
        settings.pomodoroBreakMin = sender.tag
        settings.save()
        if widget?.pomoPhase == 2, widget?.pomoPaused == false {
            widget?.restartPomodoroPhase()
        }
    }

    @objc private func toggleGmail() {
        settings.showGmail.toggle()
        settings.save()
        ensureSideWidth()
        if settings.showGmail { ensureGmailFetch(force: true) }
        // OFF 時は以降の ensureGmailFetch が no-op。進行中の認証待ちはタイムアウト／ユーザー操作待ち。
    }

    @objc private func gmailConnect() {
        var creds = GmailAppCredentials.load()
        if creds.clientId.isEmpty {
            gmailEditCredentials()
            creds = GmailAppCredentials.load()
            if creds.clientId.isEmpty { return }
        }
        widget?.gmailStatus = "ブラウザで認証中…"
        widget?.needsDisplay = true
        let cid = creds.clientId
        let secret = creds.clientSecret
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            let ok = self.gmail.authorizeInteractive(clientId: cid, clientSecret: secret)
            DispatchQueue.main.async {
                self.widget?.gmailAuthorized = self.gmail.isAuthorized
                self.widget?.gmailStatus = ok ? "接続済み" : (self.gmail.lastError.isEmpty ? "認証失敗" : self.gmail.lastError)
                self.widget?.needsDisplay = true
                if ok { self.ensureGmailFetch(force: true) }
            }
        }
    }

    @objc private func gmailDisconnect() {
        gmail.clearTokens()
        widget?.gmailItems = []
        widget?.gmailAuthorized = false
        widget?.gmailStatus = "未接続"
        widget?.needsDisplay = true
    }

    @objc private func gmailEditCredentials() {
        let current = GmailAppCredentials.load()
        let alert = NSAlert()
        alert.messageText = "Gmail OAuth 設定"
        alert.informativeText = "Google Cloud の Desktop OAuth クライアントを入力してください。Keychain に保存します（settings.ini には書きません）。"
        alert.addButton(withTitle: "保存")
        alert.addButton(withTitle: "取消")
        let width: CGFloat = 360
        let stack = NSStackView()
        stack.orientation = .vertical
        stack.spacing = 6
        stack.frame = NSRect(x: 0, y: 0, width: width, height: 64)
        let idField = NSTextField(string: current.clientId)
        idField.placeholderString = "Client ID"
        let secretField = NSSecureTextField(string: current.clientSecret)
        secretField.placeholderString = "Client Secret（任意）"
        stack.addArrangedSubview(idField)
        stack.addArrangedSubview(secretField)
        alert.accessoryView = stack
        if alert.runModal() == .alertFirstButtonReturn {
            let id = idField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            let secret = secretField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            if id.isEmpty { GmailAppCredentials.delete() }
            else { GmailAppCredentials.save(clientId: id, clientSecret: secret) }
            settings.save() // 旧 ini の平文キーを落とす
        }
    }

    @objc private func gmailRefreshNow() { ensureGmailFetch(force: true) }

    @objc private func setGmailCount(_ sender: NSMenuItem) {
        settings.gmailCount = sender.tag
        settings.save()
        ensureSideWidth()
        ensureGmailFetch(force: true)
    }

    @objc private func setGmailInterval(_ sender: NSMenuItem) {
        settings.gmailIntervalSec = sender.tag
        settings.save()
        gmailNextFetch = .distantPast
        ensureGmailFetch(force: true)
    }

    private static func formatGmailInterval(_ sec: Int) -> String {
        if sec < 60 { return "\(sec) 秒" }
        if sec % 60 == 0 { return "\(sec / 60) 分" }
        return "\(sec / 60) 分 \(sec % 60) 秒"
    }

    private func ensureGmailFetch(force: Bool) {
        guard settings.showGmail else { return }
        if !gmail.isAuthorized {
            widget?.gmailAuthorized = false
            widget?.gmailStatus = "未接続"
            return
        }
        if !force, Date() < gmailNextFetch { return }
        if gmailFetching { return }
        gmailFetching = true
        let interval = max(15, min(3600, settings.gmailIntervalSec))
        gmailNextFetch = Date().addingTimeInterval(TimeInterval(interval))
        widget?.gmailStatus = "更新中…"
        widget?.needsDisplay = true
        let creds = GmailAppCredentials.load()
        let count = settings.gmailCount
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self else { return }
            let ok = self.gmail.refreshInbox(clientId: creds.clientId, clientSecret: creds.clientSecret, count: count)
            let items = self.gmail.snapshot()
            let err = self.gmail.lastError
            DispatchQueue.main.async {
                self.gmailFetching = false
                self.widget?.gmailAuthorized = self.gmail.isAuthorized
                if ok {
                    self.widget?.gmailItems = items
                    self.widget?.gmailStatus = ""
                } else {
                    self.widget?.gmailStatus = err.isEmpty ? "取得失敗" : err
                }
                self.widget?.needsDisplay = true
            }
        }
    }

    @objc private func quit() {
        NSApp.terminate(nil)
    }
}

var keepAlive: AppDelegate?
private var instanceLockFD: Int32 = -1

func acquireInstanceLock() -> Bool {
    let dir = Settings.supportDirectory()
    do {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    } catch {
        return true
    }
    let fd = open(dir.appendingPathComponent("instance.lock").path, O_CREAT | O_RDWR, 0o644)
    if fd < 0 { return true }
    if flock(fd, LOCK_EX | LOCK_NB) != 0 {
        close(fd)
        return false
    }
    instanceLockFD = fd
    return true
}

enum SelfTest {
    private final class Tally {
        var failed = 0
        func expect(_ cond: Bool, _ message: String) {
            if cond {
                print("ok  \(message)")
            } else {
                print("NG  \(message)")
                failed += 1
            }
        }
    }

    static func run() -> Int32 {
        let tally = Tally()
        checkFormat(tally)
        checkSettings(tally)
        checkLaunchAgentPlist(tally)
        checkMetrics(tally)
        checkDrawing(tally)
        print(tally.failed == 0 ? "SELFTEST PASS" : "SELFTEST FAIL \(tally.failed)")
        return tally.failed == 0 ? 0 : 1
    }

    private static func checkFormat(_ t: Tally) {
        t.expect(Format.speed(-1, bits: false) == "--", "speed missing")
        t.expect(Format.speed(0, bits: false) == "0 B/s", "speed 0")
        t.expect(Format.speed(512, bits: false) == "512 B/s", "speed bytes")
        t.expect(Format.speed(1536, bits: false) == "1.5 KB/s", "speed KB")
        t.expect(Format.speed(100, bits: true) == "800 bps", "speed bps")
        t.expect(Format.speed(125, bits: true) == "1.0 Kbps", "speed Kbps")
        t.expect(Format.percent(-1) == "--", "percent missing")
        t.expect(Format.percent(12.4) == "12%", "percent round down")
        t.expect(Format.cpuPercent(user: 25, system: 25, idle: 50, nice: 0) == 50, "cpu 50")
        t.expect(Format.busyPercent(serviceNs: 250_000_000, elapsedNs: 1_000_000_000) == 25, "disk 25")
        t.expect(Format.busyPercent(serviceNs: 2_000_000_000, elapsedNs: 1_000_000_000) == 100, "disk clamp")
        t.expect(Format.busyPercent(serviceNs: 1, elapsedNs: 0) == -1, "disk elapsed 0")
    }

    private static func checkSettings(_ t: Tally) {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("sysmon-settings-\(getpid()).ini")
        defer { try? FileManager.default.removeItem(at: url) }
        let bad = """
        X=10
        Y=20
        Width=300
        Height=400
        TopMost=False
        Bits=True
        Opacity=5
        FontScale=0.1
        """
        try? bad.write(to: url, atomically: true, encoding: .utf8)
        let loaded = Settings.load(from: url)
        t.expect(loaded.x == 10 && loaded.y == 20, "settings position")
        t.expect(loaded.width == 300 && loaded.height == 400, "settings size")
        t.expect(loaded.topMost == false && loaded.bits == true, "settings flags")
        t.expect(abs(loaded.opacity - 1) < 0.001, "settings opacity clamp")
        t.expect(abs(loaded.fontScale - Settings.minFontScale) < 0.001, "settings font clamp")

        loaded.fontScale = 1.3
        loaded.opacity = 0.8
        loaded.bits = false
        loaded.save(to: url)
        let again = Settings.load(from: url)
        t.expect(abs(again.fontScale - 1.3) < 0.001 && abs(again.opacity - 0.8) < 0.001 && again.bits == false,
                 "settings roundtrip")
    }

    private static func checkLaunchAgentPlist(_ t: Tally) {
        do {
            let obj = try AutoStart.launchAgentPlist(executable: "/tmp/SysmonWidget")
            let args = obj["ProgramArguments"] as? [String]
            t.expect(obj["Label"] as? String == AutoStart.label, "launchd label")
            t.expect(args == ["/tmp/SysmonWidget"], "launchd program")
            t.expect(obj["RunAtLoad"] as? Bool == true, "launchd RunAtLoad")
        } catch {
            t.expect(false, "launchd plist \(error)")
        }
    }

    private static func checkMetrics(_ t: Tally) {
        let collector = MetricsCollector()
        var sawCPU = false
        var sawMem = false
        var sawDisk = false
        var sawGPU = false
        var last = Snapshot()
        for i in 0..<4 {
            let start = DispatchTime.now().uptimeNanoseconds
            let s = collector.sample()
            let ms = Double(DispatchTime.now().uptimeNanoseconds &- start) / 1e6
            print(String(format: "[%d] CPU=%.1f MEM=%.1f (%.1f/%.1fGB) DISK=%.1f GPU=%.1f WIFI=%@(%@) LINK=%.0f DOWN=%.0fB/s UP=%.0fB/s  (%.0fms)",
                         i, s.cpu, s.mem,
                         Double(s.memUsed) / 1073741824, Double(s.memTotal) / 1073741824,
                         s.disk, s.gpu,
                         s.wifiConnected ? "true" : "false", s.wifiName,
                         s.wifiLinkMbps, s.down, s.up, ms))
            if s.cpu >= 0 && s.cpu <= 100 { sawCPU = true }
            if s.mem >= 0 && s.mem <= 100 && s.memTotal > (1 << 30) && s.memUsed <= s.memTotal { sawMem = true }
            if s.disk >= 0 && s.disk <= 100 { sawDisk = true }
            if s.gpu >= 0 && s.gpu <= 100 { sawGPU = true }
            if s.wifiConnected && i >= 1 && (s.down < 0 || s.up < 0 || s.down > 5e9 || s.up > 5e9) {
                t.expect(false, "wifi speed down=\(s.down) up=\(s.up) ifaces=\(collector.interfaceDebugSummary())")
            }
            if ms > 500 { t.expect(false, "sample too slow \(ms)ms") }
            last = s
            if i < 3 { Thread.sleep(forTimeInterval: 1) }
        }
        t.expect(sawCPU, "CPU sampled")
        t.expect(sawMem, "MEM sampled")
        t.expect(sawDisk, "DISK sampled")
        if !sawGPU {
            t.expect(false, "GPU sampled")
            print(collector.gpuDebugSummary())
        } else {
            t.expect(true, "GPU sampled")
        }
        if last.wifiConnected {
            t.expect(last.wifiLinkMbps > 0, "wifi link rate")
        } else {
            print("wifi not connected; link-rate check skipped")
        }
    }

    private static func checkDrawing(_ t: Tally) {
        let settings = Settings()
        settings.fontScale = 1
        settings.bits = false
        let view = WidgetView(settings: settings)
        let size = view.defaultSize()
        view.frame = NSRect(origin: .zero, size: size)
        var snap = Snapshot()
        snap.cpu = 42
        snap.mem = 63
        snap.disk = 15
        snap.gpu = 55
        snap.memUsed = 8 * 1073741824
        snap.memTotal = 16 * 1073741824
        snap.wifiConnected = true
        snap.wifiLinkMbps = 866
        snap.down = 1_500_000
        snap.up = 80_000
        view.snapshot = snap
        view.downHist = [200, 100, 900, 300, 400]
        view.upHist = [100, 400, 200, 800, 150]

        guard let rep = render(view) else {
            t.expect(false, "render bitmap")
            return
        }
        t.expect(imageHas(rep, 28, 28, 32), "background drawn")
        t.expect(imageHas(rep, 77, 166, 255), "CPU bar drawn")
        t.expect(imageHas(rep, 178, 120, 255), "MEM bar drawn")
        t.expect(imageHas(rep, 80, 210, 140), "DISK bar drawn")
        t.expect(imageHas(rep, 255, 170, 60), "GPU bar drawn")
    }

    private static func render(_ view: WidgetView) -> NSBitmapImageRep? {
        let scale: CGFloat = 2
        let pixelsWide = Int((view.bounds.width * scale).rounded())
        let pixelsHigh = Int((view.bounds.height * scale).rounded())
        guard let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixelsWide,
            pixelsHigh: pixelsHigh,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { return nil }
        rep.size = view.bounds.size
        guard let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = ctx
        ctx.cgContext.translateBy(x: 0, y: view.bounds.height)
        ctx.cgContext.scaleBy(x: 1, y: -1)
        view.draw(view.bounds)
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }

    private static func imageHas(_ rep: NSBitmapImageRep, _ r: Int, _ g: Int, _ b: Int) -> Bool {
        let width = rep.pixelsWide
        let height = rep.pixelsHigh
        var y = 0
        while y < height {
            var x = 0
            while x < width {
                if let color = rep.colorAt(x: x, y: y)?.usingColorSpace(.sRGB), color.alphaComponent > 0.5 {
                    let rr = Int((color.redComponent * 255).rounded())
                    let gg = Int((color.greenComponent * 255).rounded())
                    let bb = Int((color.blueComponent * 255).rounded())
                    if abs(rr - r) <= 18 && abs(gg - g) <= 18 && abs(bb - b) <= 18 {
                        return true
                    }
                }
                x += 2
            }
            y += 2
        }
        return false
    }
}
