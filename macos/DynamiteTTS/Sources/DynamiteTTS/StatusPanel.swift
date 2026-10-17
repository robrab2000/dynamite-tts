import AppKit

@MainActor
final class StatusPanel {
    private let panel: NSPanel
    private let label = NSTextField(labelWithString: "")
    private let bars = NSStackView()
    private var barViews: [NSView] = []
    private var hideWork: DispatchWorkItem?
    private let onStop: () -> Void
    private var timer: Timer?

    init(onStop: @escaping () -> Void) {
        self.onStop = onStop
        panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 280, height: 44),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.hidesOnDeactivate = false

        let background = NSVisualEffectView(frame: panel.contentView!.bounds)
        background.autoresizingMask = [.width, .height]
        background.material = .hudWindow
        background.state = .active
        background.wantsLayer = true
        background.layer?.cornerRadius = 14
        panel.contentView = background

        label.font = .systemFont(ofSize: 13, weight: .medium)
        label.textColor = .labelColor
        label.lineBreakMode = .byTruncatingTail

        bars.orientation = .horizontal
        bars.spacing = 3
        bars.alignment = .centerY
        for _ in 0..<5 {
            let bar = NSView()
            bar.wantsLayer = true
            bar.layer?.backgroundColor = NSColor.controlAccentColor.cgColor
            bar.layer?.cornerRadius = 1.5
            bar.translatesAutoresizingMaskIntoConstraints = false
            bar.widthAnchor.constraint(equalToConstant: 3).isActive = true
            bar.heightAnchor.constraint(equalToConstant: 4).isActive = true
            barViews.append(bar)
            bars.addArrangedSubview(bar)
        }

        let stop = NSButton(title: "Stop", target: nil, action: nil)
        stop.bezelStyle = .inline
        stop.target = self
        stop.action = #selector(stopPressed)

        let row = NSStackView(views: [label, bars, stop])
        row.orientation = .horizontal
        row.alignment = .centerY
        row.spacing = 10
        row.translatesAutoresizingMaskIntoConstraints = false
        background.addSubview(row)
        NSLayoutConstraint.activate([
            row.leadingAnchor.constraint(equalTo: background.leadingAnchor, constant: 14),
            row.trailingAnchor.constraint(equalTo: background.trailingAnchor, constant: -10),
            row.centerYAnchor.constraint(equalTo: background.centerYAnchor),
        ])
    }

    func show(_ text: String) {
        hideWork?.cancel()
        label.stringValue = text
        place()
        panel.orderFrontRegardless()
        startMeter()
    }

    func showNotice(_ text: String) {
        show(text)
        let work = DispatchWorkItem { [weak self] in self?.hide() }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }

    func hide() {
        timer?.invalidate()
        timer = nil
        panel.orderOut(nil)
    }

    func updateLevel(_ level: Float) {
        let active = Int((level * Float(barViews.count)).rounded(.up))
        for (index, bar) in barViews.enumerated() {
            let height: CGFloat = index < active ? CGFloat(6 + index * 3) : 4
            bar.constraints.first { $0.firstAttribute == .height }?.constant = height
        }
    }

    @objc private func stopPressed() {
        onStop()
    }

    private func startMeter() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.updateLevel(AppDelegate.shared?.speech.player.level ?? 0)
            }
        }
    }

    private func place() {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        guard let screen else { return }
        let visible = screen.visibleFrame
        let size = panel.frame.size
        let origin = NSPoint(x: visible.midX - size.width / 2, y: visible.maxY - size.height - 12)
        panel.setFrameOrigin(origin)
    }
}
