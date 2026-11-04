import AppKit

if CommandLine.arguments.contains("--selftest") {
    exit(SelfTest.run())
}
if !acquireInstanceLock() {
    exit(0)
}
let application = NSApplication.shared
application.setActivationPolicy(.accessory)
let delegate = AppDelegate()
keepAlive = delegate
application.delegate = delegate
application.run()
