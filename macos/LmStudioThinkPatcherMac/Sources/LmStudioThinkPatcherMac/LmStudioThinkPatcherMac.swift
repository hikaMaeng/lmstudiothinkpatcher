import AppKit
import CryptoKit
import Foundation
import SwiftUI

@main
struct LmStudioThinkPatcherMacApp: App {
    @StateObject private var viewModel = PatcherViewModel()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(viewModel)
                .frame(minWidth: 1100, minHeight: 700)
        }
        .windowStyle(.titleBar)
    }
}

struct ContentView: View {
    @EnvironmentObject private var viewModel: PatcherViewModel

    var body: some View {
        VStack(spacing: 0) {
            toolbar
            Divider()
            tableHeader
            Divider()
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach($viewModel.rows) { $row in
                        PatchRowView(
                            row: $row,
                            isSelected: viewModel.selectedIDs.contains(row.id),
                            toggleSelection: { viewModel.toggleSelection(row.id) }
                        )
                        Divider()
                    }
                }
            }
            Divider()
            Text(viewModel.summary)
                .font(.callout)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .lineLimit(2)
        }
        .alert("LM Studio Think Patcher", isPresented: $viewModel.showAlert) {
            Button("OK", role: .cancel) {}
        } message: {
            Text(viewModel.alertMessage)
        }
        .task {
            viewModel.analyzeSavedModelRoot()
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            Text("Model root")
            TextField("LM Studio model root", text: $viewModel.modelRoot)
                .textFieldStyle(.roundedBorder)
                .onSubmit { viewModel.saveSettings() }
            Button("Browse") { viewModel.browseModelRoot() }
            Button("Analysis") { viewModel.analyze() }
            Text("Suffix")
            TextField("Suffix", text: $viewModel.suffix)
                .textFieldStyle(.roundedBorder)
                .frame(width: 120)
                .onSubmit { viewModel.saveSettings() }
            Button("Patch all") { viewModel.patchAll() }
            Button("Patch unpatched") { viewModel.patchUnpatched() }
            Button("Repatch selected") { viewModel.repatchSelected() }
            Button("Delete selected", role: .destructive) { viewModel.deleteSelected() }
        }
        .padding(10)
    }

    private var tableHeader: some View {
        HStack(spacing: 8) {
            Text("Model").frame(width: 190, alignment: .leading)
            Text("Patch name").frame(width: 220, alignment: .leading)
            Text("GGUF").frame(width: 52, alignment: .trailing)
            Text("Status").frame(width: 120, alignment: .leading)
            Text("Hub model").frame(width: 260, alignment: .leading)
            Text("Model folder").frame(maxWidth: .infinity, alignment: .leading)
        }
        .font(.caption.weight(.semibold))
        .foregroundStyle(.secondary)
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
    }
}

struct PatchRowView: View {
    @Binding var row: PatchModelRow
    let isSelected: Bool
    let toggleSelection: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Text(row.displayName)
                .frame(width: 190, alignment: .leading)
                .foregroundStyle(row.isVariation ? .secondary : .primary)
            TextField("Patch name", text: $row.patchName)
                .textFieldStyle(.roundedBorder)
                .frame(width: 220)
            Text("\(row.ggufFileCount)")
                .frame(width: 52, alignment: .trailing)
            Text(row.status)
                .frame(width: 120, alignment: .leading)
            Text(row.note)
                .frame(width: 260, alignment: .leading)
                .lineLimit(1)
                .truncationMode(.middle)
            Text(row.relativePath)
                .frame(maxWidth: .infinity, alignment: .leading)
                .lineLimit(1)
                .truncationMode(.middle)
        }
        .font(.system(size: 13))
        .padding(.horizontal, 12)
        .padding(.vertical, 5)
        .background(isSelected ? Color.accentColor.opacity(0.18) : (row.isVariation ? Color.secondary.opacity(0.06) : Color.clear))
        .contentShape(Rectangle())
        .onTapGesture { toggleSelection() }
    }
}

@MainActor
final class PatcherViewModel: ObservableObject {
    @Published var modelRoot: String
    @Published var suffix: String
    @Published var rows: [PatchModelRow] = []
    @Published var selectedIDs: Set<UUID> = []
    @Published var summary = "Select a model root and run Analysis."
    @Published var showAlert = false
    @Published var alertMessage = ""

    private let service = ModelPatchService()
    private let defaults = UserDefaults.standard

    init() {
        modelRoot = defaults.string(forKey: "modelRoot") ?? ""
        suffix = defaults.string(forKey: "suffix") ?? "-ndx"
    }

    func browseModelRoot() {
        let panel = NSOpenPanel()
        panel.title = "Select LM Studio model root"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = FileManager.default.fileExists(atPath: modelRoot) ? URL(fileURLWithPath: modelRoot) : FileManager.default.homeDirectoryForCurrentUser
        if panel.runModal() == .OK, let url = panel.url {
            modelRoot = url.path
            saveSettings()
        }
    }

    func analyze() {
        runSafely {
            saveSettings()
            rows = try service.analyze(modelRoot: modelRoot, suffix: suffix)
            selectedIDs.removeAll()
            updateSummary()
        }
    }

    func analyzeSavedModelRoot() {
        guard rows.isEmpty, !modelRoot.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return
        }

        analyze()
    }

    func patchAll() {
        patch(rows.filter(\.isPatchable))
    }

    func patchUnpatched() {
        patch(rows.filter { $0.isPatchable && !$0.isPatched })
    }

    func repatchSelected() {
        patch(rows.filter { selectedIDs.contains($0.id) })
    }

    func deleteSelected() {
        let targets = rows.filter { selectedIDs.contains($0.id) && !$0.hubModelPath.isEmpty }
        guard !targets.isEmpty else {
            alert("No selected hub patch folders.")
            return
        }

        let preview = targets.prefix(8).map(\.hubModelPath).joined(separator: "\n")
        let alert = NSAlert()
        alert.messageText = "Delete selected patch folders?"
        alert.informativeText = "\(targets.count) folder(s) will be deleted.\n\n\(preview)"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "Delete")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else {
            return
        }

        runSafely {
            for row in uniqueRowsByHubPath(targets) {
                try service.deletePatchFolder(row)
            }
            rows = try service.analyze(modelRoot: modelRoot, suffix: suffix)
            selectedIDs.removeAll()
            updateSummary()
        }
    }

    func toggleSelection(_ id: UUID) {
        if selectedIDs.contains(id) {
            selectedIDs.remove(id)
        } else {
            selectedIDs.insert(id)
        }
    }

    func saveSettings() {
        defaults.set(modelRoot.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "modelRoot")
        defaults.set(suffix.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "suffix")
    }

    private func patch(_ targets: [PatchModelRow]) {
        runSafely {
            saveSettings()
            for row in targets {
                _ = try service.patch(row, suffix: suffix)
            }
            rows = try service.analyze(modelRoot: modelRoot, suffix: suffix)
            selectedIDs.removeAll()
            updateSummary()
        }
    }

    private func updateSummary() {
        let primaryRows = rows.filter(\.isPrimaryModel)
        let patched = primaryRows.filter(\.isPatched).count
        let partial = primaryRows.filter { $0.status == "Partial" }.count
        let unpatched = primaryRows.filter { $0.status == "Unpatched" }.count
        let notApplicable = primaryRows.filter { $0.status == "Not applicable" }.count
        let variations = rows.filter(\.isVariation).count
        summary = "Model folders: \(primaryRows.count)   Variations: \(variations)   Patched: \(patched)   Partial: \(partial)   Unpatched: \(unpatched)   Not applicable: \(notApplicable)   Hub: \(service.hubModelsRoot)"
    }

    private func runSafely(_ action: () throws -> Void) {
        do {
            try action()
        } catch {
            alert(error.localizedDescription)
        }
    }

    private func alert(_ message: String) {
        alertMessage = message
        showAlert = true
    }

    private func uniqueRowsByHubPath(_ rows: [PatchModelRow]) -> [PatchModelRow] {
        var seen = Set<String>()
        return rows.filter { row in
            let path = URL(fileURLWithPath: row.hubModelPath).standardizedFileURL.path
            return seen.insert(path).inserted
        }
    }
}

struct PatchModelRow: Identifiable, Hashable {
    let id = UUID()
    var modelName = ""
    var variationName = ""
    var isVariation = false
    var patchName = ""
    var status = ""
    var note = ""
    var relativePath = ""
    var modelDirectory = ""
    var publisher = ""
    var patchPublisher = ""
    var baseModelKey = ""
    var hubModelPath = ""
    var primaryGgufFiles: [String] = []
    var templateSource = ""
    var supportsReasoningPatch = true
    var ggufFileCount = 0

    var displayName: String { isVariation ? "    \(variationName)" : modelName }
    var isPatched: Bool { status.lowercased().hasPrefix("patched") }
    var isPrimaryModel: Bool { !isVariation }
    var isPatchable: Bool { isPrimaryModel && supportsReasoningPatch }
}

final class ModelPatchService {
    let hubModelsRoot: String
    private let fileManager = FileManager.default
    private let encoder: JSONEncoder = {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }()

    init() {
        hubModelsRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".lmstudio/hub/models")
            .path
    }

    func analyze(modelRoot: String, suffix: String) throws -> [PatchModelRow] {
        let root = modelRoot.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !root.isEmpty, directoryExists(root) else {
            throw PatcherError.message("Model root folder was not found.")
        }

        try fileManager.createDirectory(atPath: hubModelsRoot, withIntermediateDirectories: true)
        let hubIndex = loadHubIndex()
        let ggufFiles = enumerateFiles(root: root, extensionName: "gguf").filter(isPrimaryGguf)
        let grouped = Dictionary(grouping: ggufFiles) { URL(fileURLWithPath: $0).deletingLastPathComponent().path }

        return try grouped.keys.sorted { $0.localizedCaseInsensitiveCompare($1) == .orderedAscending }
            .flatMap { directory in
                try createRows(
                    modelRoot: root,
                    modelDirectory: directory,
                    ggufFiles: grouped[directory] ?? [],
                    suffix: suffix,
                    hubIndex: hubIndex
                )
            }
            .sorted { left, right in
                if left.relativePath.localizedCaseInsensitiveCompare(right.relativePath) != .orderedSame {
                    return left.relativePath.localizedCaseInsensitiveCompare(right.relativePath) == .orderedAscending
                }
                if left.isVariation != right.isVariation {
                    return !left.isVariation
                }
                return left.patchName.localizedCaseInsensitiveCompare(right.patchName) == .orderedAscending
            }
    }

    func patch(_ input: PatchModelRow, suffix: String) throws -> PatchModelRow {
        var row = input
        if row.patchName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            row.patchName = makePatchName(modelName: row.modelName, suffix: suffix)
        }

        let templateSource = resolvePromptTemplate(row)
        row.templateSource = templateSource.source
        row.supportsReasoningPatch = !templateSource.isFallback && TemplateCatalog.supportsReasoningPatch(templateSource.template)
        guard row.supportsReasoningPatch else {
            row.status = "Not applicable"
            row.note = "reasoning marker missing - \(templateSource.source)"
            return row
        }

        let patchPublisher = row.patchPublisher.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? row.publisher : row.patchPublisher
        row.hubModelPath = buildHubModelPath(publisher: patchPublisher, patchName: row.patchName)
        try fileManager.createDirectory(atPath: row.hubModelPath, withIntermediateDirectories: true)

        let modelYamlPath = URL(fileURLWithPath: row.hubModelPath).appendingPathComponent("model.yaml").path
        let backupFileName = try backupExistingModelYaml(modelYamlPath)
        let originalSha = fileManager.fileExists(atPath: modelYamlPath) ? try computeSha256(modelYamlPath) : nil
        try buildModelYaml(row: row, templateSource: templateSource)
            .write(toFile: modelYamlPath, atomically: true, encoding: .utf8)

        let metadata = NdxPatchMetadata(
            createdAt: ISO8601DateFormatter().string(from: Date()),
            folderName: row.modelName,
            publisher: patchPublisher,
            baseModelKey: row.baseModelKey,
            aliasModelKey: "\(patchPublisher)/\(row.patchName)",
            outputFileName: "model.yaml",
            originalModelYamlExisted: backupFileName != nil,
            originalModelYamlSha256: originalSha,
            backupFileName: backupFileName
        )
        let metadataData = try encoder.encode(metadata)
        try metadataData.write(to: URL(fileURLWithPath: row.hubModelPath).appendingPathComponent("ndx-model-patch.json"))

        row.status = "Patched"
        row.note = "\(patchPublisher)/\(row.patchName)"
        return row
    }

    func deletePatchFolder(_ row: PatchModelRow) throws {
        guard !row.hubModelPath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return
        }

        let root = URL(fileURLWithPath: hubModelsRoot).standardizedFileURL.path
        let target = URL(fileURLWithPath: row.hubModelPath).standardizedFileURL.path
        guard target.hasPrefix(root + "/") else {
            throw PatcherError.message("Refusing to delete a folder outside hub/models.")
        }

        if directoryExists(target) {
            try fileManager.removeItem(atPath: target)
        }
    }

    private func createRows(modelRoot: String, modelDirectory: String, ggufFiles: [String], suffix: String, hubIndex: [HubModelRecord]) throws -> [PatchModelRow] {
        let relativePath = relativePath(from: modelRoot, to: modelDirectory)
        let parts = relativePath.split(separator: "/").map(String.init)
        let publisher = parts.count >= 2 ? parts[0] : "local"
        let modelName = parts.last ?? URL(fileURLWithPath: modelDirectory).lastPathComponent
        let baseModelKey = parts.count >= 2 ? "\(publisher)/\(parts.dropFirst().joined(separator: "/"))" : "\(publisher)/\(modelName)"
        var row = PatchModelRow(
            modelName: modelName,
            patchName: makePatchName(modelName: modelName, suffix: suffix),
            relativePath: relativePath,
            modelDirectory: modelDirectory,
            publisher: publisher,
            patchPublisher: publisher,
            baseModelKey: baseModelKey,
            primaryGgufFiles: ggufFiles,
            ggufFileCount: ggufFiles.count
        )

        let variations = findVariations(row: row, hubIndex: hubIndex)
        updateStatus(row: &row, variations: variations)
        if !variations.isEmpty, !row.hubModelPath.isEmpty {
            row.patchName = URL(fileURLWithPath: row.hubModelPath).lastPathComponent
        }
        annotateReasoningPatchability(row: &row)

        var rows = [row]
        if variations.count > 1 {
            rows.append(contentsOf: variations.map { createVariationRow(parent: row, variation: $0) })
        }
        return rows
    }

    private func createVariationRow(parent: PatchModelRow, variation: HubModelRecord) -> PatchModelRow {
        var row = PatchModelRow(
            modelName: parent.modelName,
            variationName: variation.patchName,
            isVariation: true,
            patchName: variation.patchName,
            relativePath: parent.relativePath,
            modelDirectory: parent.modelDirectory,
            publisher: parent.publisher,
            patchPublisher: variation.publisher,
            baseModelKey: parent.baseModelKey,
            hubModelPath: variation.directory,
            primaryGgufFiles: parent.primaryGgufFiles,
            templateSource: parent.templateSource,
            supportsReasoningPatch: parent.supportsReasoningPatch,
            ggufFileCount: 0
        )
        row.note = "\(variation.modelKey) - \(variation.matchReason)"

        guard variation.hasPatchMetadata else {
            row.status = "Existing variation"
            row.note += " (ndx-model-patch.json missing)"
            return row
        }

        let modelYamlPath = URL(fileURLWithPath: variation.directory).appendingPathComponent("model.yaml").path
        guard fileManager.fileExists(atPath: modelYamlPath) else {
            row.status = "Partial variation"
            row.note += " (model.yaml missing)"
            return row
        }

        if (try? String(contentsOfFile: modelYamlPath, encoding: .utf8).contains("| safe")) == true {
            row.status = "Partial variation"
            row.note += " (safe filter remains)"
            return row
        }

        row.status = "Patched variation"
        return row
    }

    private func updateStatus(row: inout PatchModelRow, variations: [HubModelRecord]) {
        guard !variations.isEmpty else {
            row.status = "Unpatched"
            row.note = "hub/models variation missing"
            return
        }

        let patched = variations.filter(\.hasPatchMetadata)
        let preferred = patched.first ?? variations[0]
        row.hubModelPath = preferred.directory
        row.patchPublisher = preferred.publisher
        row.note = variations.count == 1 ? "\(preferred.modelKey) - \(preferred.matchReason)" : "\(variations.count) variations"

        guard !patched.isEmpty else {
            row.status = "Partial"
            row.note += " (ndx-model-patch.json missing)"
            return
        }

        if let broken = patched.first(where: { variation in
            let modelYamlPath = URL(fileURLWithPath: variation.directory).appendingPathComponent("model.yaml").path
            if !fileManager.fileExists(atPath: modelYamlPath) {
                return true
            }
            return (try? String(contentsOfFile: modelYamlPath, encoding: .utf8).contains("| safe")) == true
        }) {
            row.status = "Partial"
            let modelYamlPath = URL(fileURLWithPath: broken.directory).appendingPathComponent("model.yaml").path
            row.note = fileManager.fileExists(atPath: modelYamlPath) ? "\(broken.modelKey) (safe filter remains)" : "\(broken.modelKey) (model.yaml missing)"
            return
        }

        row.status = "Patched"
    }

    private func annotateReasoningPatchability(row: inout PatchModelRow) {
        let templateSource = resolvePromptTemplate(row)
        row.templateSource = templateSource.source
        row.supportsReasoningPatch = !templateSource.isFallback && TemplateCatalog.supportsReasoningPatch(templateSource.template)
        if !row.supportsReasoningPatch {
            row.status = "Not applicable"
            row.note = "reasoning marker missing - \(templateSource.source)"
        }
    }

    private func loadHubIndex() -> [HubModelRecord] {
        var records: [String: HubModelRecord] = [:]
        guard directoryExists(hubModelsRoot) else {
            return []
        }

        for modelYamlPath in enumerateFiles(root: hubModelsRoot, fileName: "model.yaml") {
            let directory = URL(fileURLWithPath: modelYamlPath).deletingLastPathComponent().path
            let relative = relativePath(from: hubModelsRoot, to: directory)
            let parts = relative.split(separator: "/").map(String.init)
            guard !parts.isEmpty else {
                continue
            }

            let modelKey = readScalarFromYaml(path: modelYamlPath, key: "model") ?? relative
            records[directory] = HubModelRecord(
                modelKey: modelKey,
                publisher: parts[0],
                patchName: parts.last ?? URL(fileURLWithPath: directory).lastPathComponent,
                directory: directory,
                baseModelKeys: readSlashValuesFromYaml(path: modelYamlPath, marker: "- key:"),
                sourceRepos: readScalarValuesFromYaml(path: modelYamlPath, key: "repo"),
                hasModelYaml: true,
                hasPatchMetadata: fileManager.fileExists(atPath: URL(fileURLWithPath: directory).appendingPathComponent("ndx-model-patch.json").path),
                matchReason: "model.yaml"
            )
        }

        for patchFile in enumerateFiles(root: hubModelsRoot, fileName: "ndx-model-patch.json") {
            guard
                let data = try? Data(contentsOf: URL(fileURLWithPath: patchFile)),
                let metadata = try? JSONDecoder().decode(NdxPatchMetadata.self, from: data),
                let baseModelKey = metadata.baseModelKey,
                let aliasModelKey = metadata.aliasModelKey
            else {
                continue
            }

            let directory = URL(fileURLWithPath: patchFile).deletingLastPathComponent().path
            let parts = aliasModelKey.split(separator: "/").map(String.init)
            let publisher = parts.count >= 2 ? parts[0] : (metadata.publisher ?? "local")
            let patchName = parts.count >= 2 ? (parts.last ?? URL(fileURLWithPath: directory).lastPathComponent) : URL(fileURLWithPath: directory).lastPathComponent

            if var existing = records[directory] {
                if !existing.baseModelKeys.contains(where: { $0.caseInsensitiveCompare(baseModelKey) == .orderedSame }) {
                    existing.baseModelKeys.append(baseModelKey)
                }
                existing.hasPatchMetadata = true
                records[directory] = existing
                continue
            }

            records[directory] = HubModelRecord(
                modelKey: aliasModelKey,
                publisher: publisher,
                patchName: patchName,
                directory: directory,
                baseModelKeys: [baseModelKey],
                sourceRepos: metadata.folderName.map { [$0] } ?? [],
                hasModelYaml: fileManager.fileExists(atPath: URL(fileURLWithPath: directory).appendingPathComponent("model.yaml").path),
                hasPatchMetadata: true,
                matchReason: "ndx metadata"
            )
        }

        return records.values.sorted { $0.modelKey.localizedCaseInsensitiveCompare($1.modelKey) == .orderedAscending }
    }

    private func findVariations(row: PatchModelRow, hubIndex: [HubModelRecord]) -> [HubModelRecord] {
        let baseKey = normalizeVariationName(row.baseModelKey)
        let modelName = normalizeVariationName(row.modelName)
        var variations: [HubModelRecord] = []

        for record in hubIndex {
            var matchReason = ""
            if record.baseModelKeys.contains(where: { normalizeVariationName($0) == baseKey }) {
                matchReason = "base key"
            } else if record.sourceRepos.contains(where: { normalizeVariationName($0) == modelName }) {
                matchReason = "source repo"
            } else if normalizeVariationName(record.patchName).hasPrefix(modelName) {
                matchReason = "prefix"
            } else if record.baseModelKeys.contains(where: { normalizeVariationName($0).hasSuffix("/" + modelName) }) {
                matchReason = "base model name"
            }

            if !matchReason.isEmpty {
                var next = record
                next.matchReason = matchReason
                variations.append(next)
            }
        }

        return variations.sorted { left, right in
            if left.hasPatchMetadata != right.hasPatchMetadata { return left.hasPatchMetadata }
            if left.hasModelYaml != right.hasModelYaml { return left.hasModelYaml }
            return left.modelKey.localizedCaseInsensitiveCompare(right.modelKey) == .orderedAscending
        }
    }

    private func buildHubModelPath(publisher: String, patchName: String) -> String {
        URL(fileURLWithPath: hubModelsRoot)
            .appendingPathComponent(sanitizePathSegment(publisher))
            .appendingPathComponent(sanitizePathSegment(patchName))
            .path
    }

    private func isPrimaryGguf(_ path: String) -> Bool {
        let fileName = URL(fileURLWithPath: path).lastPathComponent.lowercased()
        return !fileName.hasPrefix("mmproj") && !fileName.contains(".mmproj") && !fileName.contains("-mmproj")
    }

    private func makePatchName(modelName: String, suffix: String) -> String {
        let suffix = suffix.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? "-ndx" : suffix.trimmingCharacters(in: .whitespacesAndNewlines)
        return modelName.lowercased().hasSuffix(suffix.lowercased()) ? modelName : modelName + suffix
    }

    private func sanitizePathSegment(_ value: String) -> String {
        let invalid = CharacterSet(charactersIn: "/\\:").union(.controlCharacters)
        return value.unicodeScalars.map { invalid.contains($0) ? "-" : String($0) }.joined().trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func normalizeVariationName(_ value: String) -> String {
        sanitizePathSegment(value).trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    private func readScalarFromYaml(path: String, key: String) -> String? {
        let prefix = key + ":"
        return readLines(path).compactMap { line -> String? in
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.hasPrefix(prefix) else { return nil }
            return String(trimmed.dropFirst(prefix.count)).trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
        }.first
    }

    private func readScalarValuesFromYaml(path: String, key: String) -> [String] {
        let prefix = key + ":"
        return readLines(path).compactMap { line -> String? in
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.hasPrefix(prefix) else { return nil }
            return String(trimmed.dropFirst(prefix.count)).trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
        }
    }

    private func readSlashValuesFromYaml(path: String, marker: String) -> [String] {
        readLines(path).compactMap { line -> String? in
            let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
            guard let range = trimmed.range(of: marker) else { return nil }
            let value = String(trimmed[range.upperBound...]).trimmingCharacters(in: .whitespacesAndNewlines).trimmingCharacters(in: CharacterSet(charactersIn: "\"'"))
            return value.contains("/") ? value : nil
        }
    }

    private func backupExistingModelYaml(_ modelYamlPath: String) throws -> String? {
        guard fileManager.fileExists(atPath: modelYamlPath) else {
            return nil
        }

        let backupFileName = "model.yaml.ndx-backup." + ISO8601DateFormatter().string(from: Date()).replacingOccurrences(of: ":", with: "-")
        let backupPath = URL(fileURLWithPath: modelYamlPath).deletingLastPathComponent().appendingPathComponent(backupFileName).path
        try fileManager.copyItem(atPath: modelYamlPath, toPath: backupPath)
        return backupFileName
    }

    private func computeSha256(_ path: String) throws -> String {
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        let digest = SHA256.hash(data: data)
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private func buildModelYaml(row: PatchModelRow, templateSource: PromptTemplateSource) -> String {
        let patchPublisher = row.patchPublisher.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? row.publisher : row.patchPublisher
        let modelKey = "\(patchPublisher)/\(row.patchName)"
        let template = indentBlock(TemplateCatalog.patchForReasoningEffortLow(templateSource.template), spaces: 14)
        let markers = TemplateCatalog.getReasoningMarkers(templateSource.template)
        let sourceUser = row.baseModelKey.contains("/") ? (row.baseModelKey.split(separator: "/").first.map(String.init) ?? row.publisher) : row.publisher

        return """
        # Generated by LM Studio Think Patcher. Do not place this file inside the raw GGUF model folder.
        # Prompt template source: \(templateSource.source)
        model: \(modelKey)
        base:
          - key: \(row.baseModelKey)
            sources:
              - type: huggingface
                user: \(sourceUser)
                repo: \(row.modelName)
        metadataOverrides:
          domain: llm
          compatibilityTypes:
            - gguf
          reasoning: true
          trainedForToolUse: true
        customFields:
          - key: enableThinking
            displayName: Enable Thinking
            description: Controls whether the prompt template opens the assistant thinking channel.
            type: boolean
            defaultValue: false
            effects:
              - type: setJinjaVariable
                variable: enable_thinking
        config:
          operation:
            fields:
              - key: llm.prediction.reasoning.parsing
                value:
                  enabled: true
                  startString: "\(escapeYamlDoubleQuoted(markers.start))"
                  endString: "\(escapeYamlDoubleQuoted(markers.end))"
              - key: llm.prediction.promptTemplate
                value:
                  type: jinja
                  jinjaPromptTemplate:
                    template: |-
        \(template)
                  stopStrings: []
        """
    }

    private func escapeYamlDoubleQuoted(_ value: String) -> String {
        value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    }

    private func resolvePromptTemplate(_ row: PatchModelRow) -> PromptTemplateSource {
        for source in candidateHubYamlFiles(row) {
            if let template = extractYamlLiteralBlock(path: source, key: "template"), !template.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return PromptTemplateSource(template: template, source: source, isFallback: false)
            }
        }

        for source in candidateInternalConfigFiles(row) {
            if let template = extractJinjaTemplateFromJson(path: source), !template.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return PromptTemplateSource(template: template, source: source, isFallback: false)
            }
        }

        for ggufFile in row.primaryGgufFiles {
            if let template = tryReadGgufChatTemplate(ggufFile), !template.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                return PromptTemplateSource(template: template, source: "\(ggufFile):tokenizer.chat_template", isFallback: false)
            }
        }

        return PromptTemplateSource(template: TemplateCatalog.fallbackTemplate, source: "fallback: built-in ChatML", isFallback: true)
    }

    private func candidateHubYamlFiles(_ row: PatchModelRow) -> [String] {
        guard !row.hubModelPath.isEmpty, directoryExists(row.hubModelPath) else {
            return []
        }

        var candidates = enumerateFiles(root: row.hubModelPath, prefix: "model.yaml.ndx-backup.").sorted()
        let current = URL(fileURLWithPath: row.hubModelPath).appendingPathComponent("model.yaml").path
        if fileManager.fileExists(atPath: current) {
            candidates.append(current)
        }
        return candidates.filter { !isGeneratedYaml($0) }
    }

    private func isGeneratedYaml(_ path: String) -> Bool {
        readLines(path).prefix(3).contains { $0.localizedCaseInsensitiveContains("Generated by LM Studio Think Patcher") }
    }

    private func candidateInternalConfigFiles(_ row: PatchModelRow) -> [String] {
        let configRoot = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".lmstudio/.internal/user-concrete-model-default-config")
            .path
        let relativeParts = row.relativePath.split(separator: "/").map { sanitizePathSegment(String($0)) }
        var candidates: [String] = []

        if !relativeParts.isEmpty {
            let modelConfigDirectory = relativeParts.reduce(configRoot) { URL(fileURLWithPath: $0).appendingPathComponent($1).path }
            for ggufFile in row.primaryGgufFiles {
                let configFile = URL(fileURLWithPath: modelConfigDirectory).appendingPathComponent(URL(fileURLWithPath: ggufFile).lastPathComponent + ".json").path
                if fileManager.fileExists(atPath: configFile) {
                    candidates.append(configFile)
                }
            }
        }

        let aliasFile = URL(fileURLWithPath: configRoot)
            .appendingPathComponent(sanitizePathSegment(row.patchPublisher))
            .appendingPathComponent(sanitizePathSegment(row.patchName) + ".json")
            .path
        if fileManager.fileExists(atPath: aliasFile) {
            candidates.append(aliasFile)
        }

        return candidates
    }

    private func extractJinjaTemplateFromJson(path: String) -> String? {
        guard
            let data = try? Data(contentsOf: URL(fileURLWithPath: path)),
            let object = try? JSONSerialization.jsonObject(with: data)
        else {
            return nil
        }
        return findJinjaTemplate(object)
    }

    private func findJinjaTemplate(_ object: Any) -> String? {
        if let dictionary = object as? [String: Any] {
            if
                let jinja = dictionary["jinjaPromptTemplate"] as? [String: Any],
                let template = jinja["template"] as? String
            {
                return template
            }
            for value in dictionary.values {
                if let found = findJinjaTemplate(value) {
                    return found
                }
            }
        } else if let array = object as? [Any] {
            for value in array {
                if let found = findJinjaTemplate(value) {
                    return found
                }
            }
        }
        return nil
    }

    private func extractYamlLiteralBlock(path: String, key: String) -> String? {
        let lines = readLines(path)
        for index in lines.indices {
            let trimmed = lines[index].trimmingCharacters(in: .whitespacesAndNewlines)
            guard trimmed.hasPrefix(key + ":"), trimmed.contains("|") else {
                continue
            }

            var block: [String] = []
            let keyIndent = countLeadingSpaces(lines[index])
            var lineIndex = index + 1
            while lineIndex < lines.count {
                let line = lines[lineIndex]
                if !line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, countLeadingSpaces(line) <= keyIndent {
                    break
                }
                block.append(line)
                lineIndex += 1
            }

            while block.first?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == true {
                block.removeFirst()
            }

            let indent = block.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
                .map(countLeadingSpaces)
                .min() ?? 0
            return block.map { line in
                line.count >= indent ? String(line.dropFirst(indent)) : ""
            }.joined(separator: "\n")
        }
        return nil
    }

    private func tryReadGgufChatTemplate(_ path: String) -> String? {
        do {
            let reader = try GgufReader(path: path)
            defer { try? reader.close() }
            guard try reader.readAscii(count: 4) == "GGUF" else {
                return nil
            }
            _ = try reader.readUInt32()
            _ = try reader.readUInt64()
            let metadataCount = try reader.readUInt64()
            for _ in 0..<metadataCount {
                let key = try reader.readString()
                let valueType = try reader.readUInt32()
                if key == "tokenizer.chat_template", valueType == 8 {
                    return try reader.readString()
                }
                try reader.skipValue(type: valueType)
            }
        } catch {
            return nil
        }
        return nil
    }

    private func enumerateFiles(root: String, extensionName: String) -> [String] {
        guard let enumerator = fileManager.enumerator(atPath: root) else {
            return []
        }
        return enumerator.compactMap { item -> String? in
            guard let item = item as? String, item.lowercased().hasSuffix("." + extensionName.lowercased()) else {
                return nil
            }
            return URL(fileURLWithPath: root).appendingPathComponent(item).path
        }
    }

    private func enumerateFiles(root: String, fileName: String) -> [String] {
        guard let enumerator = fileManager.enumerator(atPath: root) else {
            return []
        }
        return enumerator.compactMap { item -> String? in
            guard let item = item as? String, URL(fileURLWithPath: item).lastPathComponent == fileName else {
                return nil
            }
            return URL(fileURLWithPath: root).appendingPathComponent(item).path
        }
    }

    private func enumerateFiles(root: String, prefix: String) -> [String] {
        guard let enumerator = fileManager.enumerator(atPath: root) else {
            return []
        }
        return enumerator.compactMap { item -> String? in
            guard let item = item as? String, URL(fileURLWithPath: item).lastPathComponent.hasPrefix(prefix) else {
                return nil
            }
            return URL(fileURLWithPath: root).appendingPathComponent(item).path
        }
    }

    private func relativePath(from root: String, to path: String) -> String {
        let rootPath = URL(fileURLWithPath: root).standardizedFileURL.path
        let targetPath = URL(fileURLWithPath: path).standardizedFileURL.path
        if targetPath == rootPath {
            return "."
        }
        if targetPath.hasPrefix(rootPath + "/") {
            return String(targetPath.dropFirst(rootPath.count + 1))
        }
        return targetPath
    }

    private func readLines(_ path: String) -> [String] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else {
            return []
        }
        return text.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
    }

    private func countLeadingSpaces(_ value: String) -> Int {
        value.prefix { $0 == " " }.count
    }

    private func indentBlock(_ value: String, spaces: Int) -> String {
        let indent = String(repeating: " ", count: spaces)
        return value.replacingOccurrences(of: "\r\n", with: "\n").split(separator: "\n", omittingEmptySubsequences: false).map { indent + $0 }.joined(separator: "\n")
    }

    private func directoryExists(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return fileManager.fileExists(atPath: path, isDirectory: &isDirectory) && isDirectory.boolValue
    }
}

enum PatcherError: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case .message(let message):
            return message
        }
    }
}

struct HubModelRecord {
    var modelKey: String
    var publisher: String
    var patchName: String
    var directory: String
    var baseModelKeys: [String]
    var sourceRepos: [String]
    var hasModelYaml: Bool
    var hasPatchMetadata: Bool
    var matchReason: String
}

struct PromptTemplateSource {
    var template: String
    var source: String
    var isFallback: Bool
}

struct NdxPatchMetadata: Codable {
    var version = 1
    var createdAt: String?
    var folderName: String?
    var publisher: String?
    var baseModelKey: String?
    var aliasModelKey: String?
    var outputFileName: String?
    var originalModelYamlExisted = false
    var originalModelYamlSha256: String?
    var backupFileName: String?

    enum CodingKeys: String, CodingKey {
        case version = "Version"
        case createdAt = "CreatedAt"
        case folderName = "FolderName"
        case publisher = "Publisher"
        case baseModelKey = "BaseModelKey"
        case aliasModelKey = "AliasModelKey"
        case outputFileName = "OutputFileName"
        case originalModelYamlExisted = "OriginalModelYamlExisted"
        case originalModelYamlSha256 = "OriginalModelYamlSha256"
        case backupFileName = "BackupFileName"
    }
}

enum TemplateCatalog {
    private static let lowEffortGuard = "{%- set ndx_low_effort = reasoning_effort is defined and reasoning_effort in ['none', 'minimal', 'low'] -%}\n{%- if ndx_low_effort -%}{%- set enable_thinking = false -%}{%- set thinking_mode = 'disabled' -%}{%- endif -%}\n"

    static func supportsReasoningPatch(_ template: String) -> Bool {
        let lowered = template.lowercased()
        return lowered.contains("reasoning_effort") ||
            lowered.contains("enable_thinking") ||
            lowered.contains("thinking_mode") ||
            lowered.contains("reasoning_content") ||
            lowered.contains("<think") ||
            lowered.contains("</think") ||
            lowered.contains("<mm:think") ||
            lowered.contains("</mm:think")
    }

    static func patchForReasoningEffortLow(_ template: String) -> String {
        var next = template.replacingOccurrences(of: "| safe", with: "")
        next = next.replacingOccurrences(of: "reasoning_effort is defined and reasoning_effort == 'low'", with: "reasoning_effort is defined and reasoning_effort in ['none', 'minimal', 'low']")
        next = next.replacingOccurrences(of: "reasoning_effort != 'low'", with: "reasoning_effort not in ['none', 'minimal', 'low']")
        next = next.replacingOccurrences(of: "{%- set ns_flags = namespace(enable_thinking=true) %}", with: "{%- set ns_flags = namespace(enable_thinking=false) %}")
        next = next.replacingOccurrences(of: "{%- set preserve_thinking = preserve_thinking | default(true) %}", with: "{%- set preserve_thinking = preserve_thinking | default(false) %}")
        next = next.replacingOccurrences(of: "{{- '<|im_start|>assistant\\n<think>\\n' }}", with: "{%- if ndx_low_effort -%}{{- '<|im_start|>assistant\\n' }}{%- else -%}{{- '<|im_start|>assistant\\n<think>\\n' }}{%- endif -%}")
        next = next.replacingOccurrences(of: "{{- '<|im_start|>assistant\\n<think></think>' }}", with: "{{- '<|im_start|>assistant\\n' }}")
        return next.hasPrefix("{%- set ndx_low_effort =") ? next : lowEffortGuard + next
    }

    static func getReasoningMarkers(_ template: String) -> (start: String, end: String) {
        let start = extractJinjaStringAssignment(template, variableName: "think_begin_token") ??
            extractJinjaStringAssignment(template, variableName: "think_start_token")
        let end = extractJinjaStringAssignment(template, variableName: "think_end_token")
        if let start, let end, !start.isEmpty, !end.isEmpty {
            return (start, end)
        }

        let lowered = template.lowercased()
        if lowered.contains("<mm:think>") || lowered.contains("</mm:think>") {
            return ("<mm:think>", "</mm:think>")
        }
        if lowered.contains("<thinking>") || lowered.contains("</thinking>") {
            return ("<thinking>", "</thinking>")
        }
        return ("<think>", "</think>")
    }

    private static func extractJinjaStringAssignment(_ template: String, variableName: String) -> String? {
        let pattern = "set\\s+" + NSRegularExpression.escapedPattern(for: variableName) + "\\s*=\\s*(['\"])(.*?)\\1"
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive, .dotMatchesLineSeparators]) else {
            return nil
        }
        let range = NSRange(template.startIndex..<template.endIndex, in: template)
        guard let match = regex.firstMatch(in: template, range: range), match.numberOfRanges >= 3 else {
            return nil
        }
        guard let valueRange = Range(match.range(at: 2), in: template) else {
            return nil
        }
        return String(template[valueRange])
    }

    static let fallbackTemplate = """
    {%- macro render_content(content) -%}
    {%- if content is string -%}
    {{- content -}}
    {%- elif content is iterable and content is not mapping -%}
    {%- for item in content -%}
    {%- if item.type == 'text' or 'text' in item -%}{{- item.text -}}{%- endif -%}
    {%- endfor -%}
    {%- elif content is none or content is undefined -%}
    {{- '' -}}
    {%- else -%}
    {{- content | string -}}
    {%- endif -%}
    {%- endmacro -%}
    {{- bos_token if bos_token is defined else '' -}}
    {%- for message in messages -%}
    {%- set role = 'system' if message.role == 'developer' else message.role -%}
    {%- if role == 'tool' -%}
    {{- '<|im_start|>user\\n<tool_response>\\n' + render_content(message.content) + '\\n</tool_response><|im_end|>\\n' -}}
    {%- elif role in ['system', 'user', 'assistant'] -%}
    {{- '<|im_start|>' + role + '\\n' + render_content(message.content) + '<|im_end|>\\n' -}}
    {%- endif -%}
    {%- endfor -%}
    {%- if add_generation_prompt -%}
    {%- if enable_thinking is defined and enable_thinking -%}
    {{- '<|im_start|>assistant\\n<think>\\n' -}}
    {%- else -%}
    {{- '<|im_start|>assistant\\n' -}}
    {%- endif -%}
    {%- endif -%}
    """
}

final class GgufReader {
    private let handle: FileHandle

    init(path: String) throws {
        handle = try FileHandle(forReadingFrom: URL(fileURLWithPath: path))
    }

    func close() throws {
        try handle.close()
    }

    func readAscii(count: Int) throws -> String {
        String(data: try readData(count: count), encoding: .ascii) ?? ""
    }

    func readUInt32() throws -> UInt32 {
        let bytes = [UInt8](try readData(count: 4))
        return UInt32(bytes[0]) | UInt32(bytes[1]) << 8 | UInt32(bytes[2]) << 16 | UInt32(bytes[3]) << 24
    }

    func readUInt64() throws -> UInt64 {
        let bytes = [UInt8](try readData(count: 8))
        var value: UInt64 = 0
        for (index, byte) in bytes.enumerated() {
            value |= UInt64(byte) << UInt64(index * 8)
        }
        return value
    }

    func readString() throws -> String {
        let length = try readUInt64()
        guard length <= UInt64(Int.max) else {
            throw PatcherError.message("Invalid GGUF string length.")
        }
        return String(data: try readData(count: Int(length)), encoding: .utf8) ?? ""
    }

    func skipValue(type: UInt32) throws {
        switch type {
        case 0, 1, 7:
            try skip(bytes: 1)
        case 2, 3:
            try skip(bytes: 2)
        case 4, 5, 6:
            try skip(bytes: 4)
        case 8:
            try skip(bytes: Int(try readUInt64()))
        case 10, 11, 12:
            try skip(bytes: 8)
        case 9:
            let elementType = try readUInt32()
            let count = try readUInt64()
            for _ in 0..<count {
                try skipValue(type: elementType)
            }
        default:
            throw PatcherError.message("Unsupported GGUF metadata value type: \(type)")
        }
    }

    private func readData(count: Int) throws -> Data {
        let data = try handle.read(upToCount: count) ?? Data()
        guard data.count == count else {
            throw PatcherError.message("Unexpected end of GGUF file.")
        }
        return data
    }

    private func skip(bytes: Int) throws {
        let offset = try handle.offset()
        try handle.seek(toOffset: offset + UInt64(bytes))
    }
}
