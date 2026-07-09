// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "LmStudioThinkPatcherMac",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "LmStudioThinkPatcherMac", targets: ["LmStudioThinkPatcherMac"])
    ],
    targets: [
        .executableTarget(name: "LmStudioThinkPatcherMac")
    ]
)
