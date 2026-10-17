// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DynamiteTTS",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "DynamiteTTS",
            path: "Sources/DynamiteTTS",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("AVFAudio"),
                .linkedFramework("CoreAudio"),
                .linkedFramework("AudioToolbox"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("Carbon"),
                .linkedFramework("ServiceManagement"),
            ]
        )
    ]
)
