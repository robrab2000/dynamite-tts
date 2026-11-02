// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "DynamiteTTS",
    platforms: [.macOS(.v14)],
    targets: [
        // Types of the Phonon shim's C ABI (windows/native/phonon/include/dynamite_phonon.h).
        // The library itself, libdynamite_phonon.dylib, is loaded at run time; see PhononEngine.swift.
        .systemLibrary(
            name: "CDynamitePhonon",
            path: "Sources/CDynamitePhonon"
        ),
        .executableTarget(
            name: "DynamiteTTS",
            dependencies: ["CDynamitePhonon"],
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
        ),
        // Drives the real Phonon shim and model; skips when either is missing.
        .testTarget(
            name: "DynamiteTTSTests",
            dependencies: ["DynamiteTTS"],
            path: "Tests/DynamiteTTSTests"
        ),
    ]
)
