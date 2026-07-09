using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LmStudioThinkPatcher;

sealed class ModelPatchService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string HubModelsRoot { get; }

    public ModelPatchService()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        HubModelsRoot = Path.Combine(userProfile, ".lmstudio", "hub", "models");
    }

    public List<PatchModelRow> Analyze(string modelRoot, string suffix)
    {
        if (string.IsNullOrWhiteSpace(modelRoot) || !Directory.Exists(modelRoot))
        {
            throw new DirectoryNotFoundException("모델 루트 폴더를 찾을 수 없습니다.");
        }

        Directory.CreateDirectory(HubModelsRoot);
        var hubIndex = LoadHubIndex();
        return Directory.EnumerateFiles(modelRoot, "*.gguf", SearchOption.AllDirectories)
            .Where(IsPrimaryGguf)
            .GroupBy(Path.GetDirectoryName, StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .SelectMany(group => CreateRows(modelRoot, group.Key!, group.ToList(), suffix, hubIndex))
            .OrderBy(row => row.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.IsVariation ? 1 : 0)
            .ThenBy(row => row.PatchName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Patch(PatchModelRow row, string suffix)
    {
        if (string.IsNullOrWhiteSpace(row.PatchName))
        {
            row.PatchName = MakePatchName(row.ModelName, suffix);
        }

        var templateSource = ResolvePromptTemplate(row);
        row.TemplateSource = templateSource.Source;
        row.SupportsReasoningPatch = !templateSource.IsFallback && TemplateCatalog.SupportsReasoningPatch(templateSource.Template);
        if (!row.SupportsReasoningPatch)
        {
            row.Status = "Not applicable";
            row.Note = $"reasoning marker 없음 - {templateSource.Source}";
            return;
        }

        var patchPublisher = string.IsNullOrWhiteSpace(row.PatchPublisher) ? row.Publisher : row.PatchPublisher;
        row.HubModelPath = BuildHubModelPath(patchPublisher, row.PatchName);
        Directory.CreateDirectory(row.HubModelPath);

        var modelYamlPath = Path.Combine(row.HubModelPath, "model.yaml");
        var backupFileName = BackupExistingModelYaml(modelYamlPath);
        var originalSha = File.Exists(modelYamlPath) ? ComputeSha256(modelYamlPath) : null;

        File.WriteAllText(modelYamlPath, BuildModelYaml(row, templateSource), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(row.HubModelPath, "ndx-model-patch.json"),
            JsonSerializer.Serialize(new NdxPatchMetadata
            {
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                FolderName = row.ModelName,
                Publisher = patchPublisher,
                BaseModelKey = row.BaseModelKey,
                AliasModelKey = $"{patchPublisher}/{row.PatchName}",
                OutputFileName = "model.yaml",
                OriginalModelYamlExisted = backupFileName is not null,
                OriginalModelYamlSha256 = originalSha,
                BackupFileName = backupFileName,
            }, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        row.Status = "Patched";
        row.Note = $"{patchPublisher}/{row.PatchName}";
    }

    public bool DeletePatchFolder(PatchModelRow row)
    {
        if (string.IsNullOrWhiteSpace(row.HubModelPath))
        {
            return false;
        }

        var root = Path.GetFullPath(HubModelsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(row.HubModelPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("hub/models 밖의 폴더는 삭제할 수 없습니다.");
        }

        if (!Directory.Exists(target))
        {
            return false;
        }

        Directory.Delete(target, recursive: true);
        return true;
    }

    private List<PatchModelRow> CreateRows(string modelRoot, string modelDirectory, List<string> ggufFiles, string suffix, List<HubModelRecord> hubIndex)
    {
        var relativePath = Path.GetRelativePath(modelRoot, modelDirectory).Replace('\\', '/');
        var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var publisher = parts.Length >= 2 ? parts[0] : "local";
        var modelName = parts.Length > 0 ? parts[^1] : Path.GetFileName(modelDirectory);
        var baseModelKey = parts.Length >= 2 ? $"{publisher}/{string.Join('/', parts.Skip(1))}" : $"{publisher}/{modelName}";
        var row = new PatchModelRow
        {
            ModelName = modelName,
            PatchName = MakePatchName(modelName, suffix),
            RelativePath = relativePath,
            ModelDirectory = modelDirectory,
            Publisher = publisher,
            PatchPublisher = publisher,
            BaseModelKey = baseModelKey,
            PrimaryGgufFiles = ggufFiles,
            GgufFileCount = ggufFiles.Count,
        };

        var variations = FindVariations(row, hubIndex);
        UpdateStatus(row, variations);
        if (variations.Count > 0 && !string.IsNullOrWhiteSpace(row.HubModelPath))
        {
            row.PatchName = Path.GetFileName(row.HubModelPath);
        }

        AnnotateReasoningPatchability(row);

        var rows = new List<PatchModelRow> { row };
        if (variations.Count > 1)
        {
            rows.AddRange(variations.Select(variation => CreateVariationRow(row, variation)));
        }

        return rows;
    }

    private static PatchModelRow CreateVariationRow(PatchModelRow parent, HubModelRecord variation)
    {
        var row = new PatchModelRow
        {
            ModelName = parent.ModelName,
            VariationName = variation.PatchName,
            IsVariation = true,
            PatchName = variation.PatchName,
            RelativePath = parent.RelativePath,
            ModelDirectory = parent.ModelDirectory,
            Publisher = parent.Publisher,
            PatchPublisher = variation.Publisher,
            BaseModelKey = parent.BaseModelKey,
            HubModelPath = variation.Directory,
            PrimaryGgufFiles = parent.PrimaryGgufFiles,
            TemplateSource = parent.TemplateSource,
            SupportsReasoningPatch = parent.SupportsReasoningPatch,
            GgufFileCount = 0,
            Note = $"{variation.ModelKey} - {variation.MatchReason}",
        };

        if (!variation.HasPatchMetadata)
        {
            row.Status = "Existing variation";
            row.Note += " (ndx-model-patch.json 없음)";
            return row;
        }

        var modelYamlPath = Path.Combine(variation.Directory, "model.yaml");
        if (!File.Exists(modelYamlPath))
        {
            row.Status = "Partial variation";
            row.Note += " (model.yaml 누락)";
            return row;
        }

        if (File.ReadAllText(modelYamlPath).Contains("| safe", StringComparison.Ordinal))
        {
            row.Status = "Partial variation";
            row.Note += " (safe 필터 남음)";
            return row;
        }

        row.Status = "Patched variation";
        return row;
    }

    private static void UpdateStatus(PatchModelRow row, List<HubModelRecord> variations)
    {
        if (variations.Count == 0)
        {
            row.Status = "Unpatched";
            row.Note = "hub/models variation 없음";
            return;
        }

        var patched = variations.Where(variation => variation.HasPatchMetadata).ToList();
        var preferred = patched.FirstOrDefault() ?? variations[0];
        row.HubModelPath = preferred.Directory;
        row.PatchPublisher = preferred.Publisher;
        row.Note = variations.Count == 1 ? $"{preferred.ModelKey} - {preferred.MatchReason}" : $"{variations.Count} variations";

        if (patched.Count == 0)
        {
            row.Status = "Partial";
            row.Note += " (ndx-model-patch.json 없음)";
            return;
        }

        var broken = patched.FirstOrDefault(variation =>
        {
            var modelYamlPath = Path.Combine(variation.Directory, "model.yaml");
            if (!File.Exists(modelYamlPath))
            {
                return true;
            }

            return File.ReadAllText(modelYamlPath).Contains("| safe", StringComparison.Ordinal);
        });
        if (broken is not null)
        {
            row.Status = "Partial";
            var modelYamlPath = Path.Combine(broken.Directory, "model.yaml");
            row.Note = File.Exists(modelYamlPath)
                ? $"{broken.ModelKey} (safe 필터 남음)"
                : $"{broken.ModelKey} (model.yaml 누락)";
            return;
        }

        row.Status = "Patched";
    }

    private void AnnotateReasoningPatchability(PatchModelRow row)
    {
        var templateSource = ResolvePromptTemplate(row);
        row.TemplateSource = templateSource.Source;
        row.SupportsReasoningPatch = !templateSource.IsFallback && TemplateCatalog.SupportsReasoningPatch(templateSource.Template);
        if (!row.SupportsReasoningPatch)
        {
            row.Status = "Not applicable";
            row.Note = $"reasoning marker 없음 - {templateSource.Source}";
        }
    }

    private List<HubModelRecord> LoadHubIndex()
    {
        var records = new Dictionary<string, HubModelRecord>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(HubModelsRoot))
        {
            return [];
        }

        foreach (var modelYamlPath in Directory.EnumerateFiles(HubModelsRoot, "model.yaml", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(modelYamlPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var relativePath = Path.GetRelativePath(HubModelsRoot, directory).Replace('\\', '/');
            var parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var modelKey = ReadScalarFromYaml(modelYamlPath, "model") ?? relativePath;
            var publisher = parts[0];
            var patchName = parts[^1];
            records[directory] = new HubModelRecord(
                ModelKey: modelKey,
                Publisher: publisher,
                PatchName: patchName,
                Directory: directory,
                BaseModelKeys: ReadSlashValuesFromYaml(modelYamlPath, "- key:"),
                SourceRepos: ReadScalarValuesFromYaml(modelYamlPath, "repo"),
                HasModelYaml: true,
                HasPatchMetadata: File.Exists(Path.Combine(directory, "ndx-model-patch.json")),
                MatchReason: "model.yaml");
        }

        foreach (var patchFile in Directory.EnumerateFiles(HubModelsRoot, "ndx-model-patch.json", SearchOption.AllDirectories))
        {
            try
            {
                var metadata = JsonSerializer.Deserialize<NdxPatchMetadata>(File.ReadAllText(patchFile));
                if (metadata?.BaseModelKey is null || metadata.AliasModelKey is null)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(patchFile)!;
                var modelKey = metadata.AliasModelKey;
                var parts = modelKey.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var publisher = parts.Length >= 2 ? parts[0] : metadata.Publisher ?? "local";
                var patchName = parts.Length >= 2 ? parts[^1] : Path.GetFileName(directory);
                if (records.TryGetValue(directory, out var existing))
                {
                    var baseKeys = existing.BaseModelKeys.Contains(metadata.BaseModelKey, StringComparer.OrdinalIgnoreCase)
                        ? existing.BaseModelKeys
                        : existing.BaseModelKeys.Concat([metadata.BaseModelKey]).ToList();
                    records[directory] = existing with { BaseModelKeys = baseKeys, HasPatchMetadata = true };
                    continue;
                }

                records[directory] = new HubModelRecord(
                    ModelKey: modelKey,
                    Publisher: publisher,
                    PatchName: patchName,
                    Directory: directory,
                    BaseModelKeys: [metadata.BaseModelKey],
                    SourceRepos: string.IsNullOrWhiteSpace(metadata.FolderName) ? [] : [metadata.FolderName],
                    HasModelYaml: File.Exists(Path.Combine(directory, "model.yaml")),
                    HasPatchMetadata: true,
                    MatchReason: "ndx metadata");
            }
            catch
            {
                // Ignore malformed third-party or hand-edited metadata files.
            }
        }

        return records.Values
            .OrderBy(record => record.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<HubModelRecord> FindVariations(PatchModelRow row, List<HubModelRecord> hubIndex)
    {
        var baseKey = NormalizeVariationName(row.BaseModelKey);
        var modelName = NormalizeVariationName(row.ModelName);
        var variations = new List<HubModelRecord>();
        foreach (var record in hubIndex)
        {
            var matchReason = "";
            if (record.BaseModelKeys.Any(key => NormalizeVariationName(key) == baseKey))
            {
                matchReason = "base key";
            }
            else if (record.SourceRepos.Any(repo => NormalizeVariationName(repo) == modelName))
            {
                matchReason = "source repo";
            }
            else if (NormalizeVariationName(record.PatchName).StartsWith(modelName, StringComparison.OrdinalIgnoreCase))
            {
                matchReason = "prefix";
            }
            else if (record.BaseModelKeys.Any(key => NormalizeVariationName(key).EndsWith("/" + modelName, StringComparison.OrdinalIgnoreCase)))
            {
                matchReason = "base model name";
            }

            if (matchReason.Length > 0)
            {
                variations.Add(record with { MatchReason = matchReason });
            }
        }

        return variations
            .OrderByDescending(variation => variation.HasPatchMetadata)
            .ThenByDescending(variation => variation.HasModelYaml)
            .ThenBy(variation => variation.ModelKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string BuildHubModelPath(string publisher, string patchName)
    {
        return Path.Combine(HubModelsRoot, SanitizePathSegment(publisher), SanitizePathSegment(patchName));
    }

    private static bool IsPrimaryGguf(string path)
    {
        var fileName = Path.GetFileName(path);
        if (fileName.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains(".mmproj", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("-mmproj", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string MakePatchName(string modelName, string suffix)
    {
        suffix = string.IsNullOrWhiteSpace(suffix) ? "-ndx" : suffix.Trim();
        return modelName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? modelName : modelName + suffix;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) || ch is '/' or '\\' or ':' ? '-' : ch).ToArray();
        return new string(chars).Trim();
    }

    private static string NormalizeVariationName(string value)
    {
        return SanitizePathSegment(value).Trim().ToLowerInvariant();
    }

    private static string? ReadScalarFromYaml(string path, string key)
    {
        var prefix = key + ":";
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed[prefix.Length..].Trim().Trim('"', '\'');
        }

        return null;
    }

    private static List<string> ReadScalarValuesFromYaml(string path, string key)
    {
        var prefix = key + ":";
        var values = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                values.Add(trimmed[prefix.Length..].Trim().Trim('"', '\''));
            }
        }

        return values;
    }

    private static List<string> ReadSlashValuesFromYaml(string path, string marker)
    {
        var values = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            var markerIndex = trimmed.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                continue;
            }

            var value = trimmed[(markerIndex + marker.Length)..].Trim().Trim('"', '\'');
            if (value.Contains('/', StringComparison.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string? BackupExistingModelYaml(string modelYamlPath)
    {
        if (!File.Exists(modelYamlPath))
        {
            return null;
        }

        var backupFileName = "model.yaml.ndx-backup." + DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        File.Copy(modelYamlPath, Path.Combine(Path.GetDirectoryName(modelYamlPath)!, backupFileName), overwrite: false);
        return backupFileName;
    }

    private static string? ComputeSha256(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private string BuildModelYaml(PatchModelRow row, PromptTemplateSource templateSource)
    {
        var patchPublisher = string.IsNullOrWhiteSpace(row.PatchPublisher) ? row.Publisher : row.PatchPublisher;
        var modelKey = $"{patchPublisher}/{row.PatchName}";
        var template = IndentBlock(TemplateCatalog.PatchForReasoningEffortLow(templateSource.Template), 14);
        var reasoningMarkers = TemplateCatalog.GetReasoningMarkers(templateSource.Template);
        var sourceUser = row.BaseModelKey.Contains('/', StringComparison.Ordinal)
            ? row.BaseModelKey.Split('/')[0]
            : row.Publisher;
        return $$"""
# Generated by LM Studio Think Patcher. Do not place this file inside the raw GGUF model folder.
# Prompt template source: {{templateSource.Source}}
model: {{modelKey}}
base:
  - key: {{row.BaseModelKey}}
    sources:
      - type: huggingface
        user: {{sourceUser}}
        repo: {{row.ModelName}}
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
          startString: "{{EscapeYamlDoubleQuoted(reasoningMarkers.Start)}}"
          endString: "{{EscapeYamlDoubleQuoted(reasoningMarkers.End)}}"
      - key: llm.prediction.promptTemplate
        value:
          type: jinja
          jinjaPromptTemplate:
            template: |-
{{template}}
          stopStrings: []
""";
    }

    private static string EscapeYamlDoubleQuoted(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private PromptTemplateSource ResolvePromptTemplate(PatchModelRow row)
    {
        foreach (var source in CandidateHubYamlFiles(row))
        {
            var template = ExtractYamlLiteralBlock(source, "template");
            if (!string.IsNullOrWhiteSpace(template))
            {
                return new PromptTemplateSource(template, source, IsFallback: false);
            }
        }

        foreach (var source in CandidateInternalConfigFiles(row))
        {
            var template = ExtractJinjaTemplateFromJson(source);
            if (!string.IsNullOrWhiteSpace(template))
            {
                return new PromptTemplateSource(template, source, IsFallback: false);
            }
        }

        foreach (var ggufFile in row.PrimaryGgufFiles)
        {
            var template = TryReadGgufChatTemplate(ggufFile);
            if (!string.IsNullOrWhiteSpace(template))
            {
                return new PromptTemplateSource(template, ggufFile + ":tokenizer.chat_template", IsFallback: false);
            }
        }

        return new PromptTemplateSource(TemplateCatalog.FallbackTemplate, "fallback: built-in ChatML", IsFallback: true);
    }

    private static IEnumerable<string> CandidateHubYamlFiles(PatchModelRow row)
    {
        if (string.IsNullOrWhiteSpace(row.HubModelPath) || !Directory.Exists(row.HubModelPath))
        {
            yield break;
        }

        foreach (var backup in Directory.EnumerateFiles(row.HubModelPath, "model.yaml.ndx-backup.*").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsGeneratedYaml(backup))
            {
                yield return backup;
            }
        }

        var current = Path.Combine(row.HubModelPath, "model.yaml");
        if (File.Exists(current))
        {
            if (!IsGeneratedYaml(current))
            {
                yield return current;
            }
        }
    }

    private static bool IsGeneratedYaml(string path)
    {
        return File.ReadLines(path)
            .Take(3)
            .Any(line => line.Contains("Generated by LM Studio Think Patcher", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> CandidateInternalConfigFiles(PatchModelRow row)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configRoot = Path.Combine(userProfile, ".lmstudio", ".internal", "user-concrete-model-default-config");
        var relativeParts = row.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizePathSegment)
            .ToArray();
        if (relativeParts.Length > 0)
        {
            var modelConfigDirectory = Path.Combine([configRoot, .. relativeParts]);
            foreach (var ggufFile in row.PrimaryGgufFiles)
            {
                var configFile = Path.Combine(modelConfigDirectory, Path.GetFileName(ggufFile) + ".json");
                if (File.Exists(configFile))
                {
                    yield return configFile;
                }
            }
        }

        var aliasFile = Path.Combine(configRoot, SanitizePathSegment(row.PatchPublisher), SanitizePathSegment(row.PatchName) + ".json");
        if (File.Exists(aliasFile))
        {
            yield return aliasFile;
        }
    }

    private static string? ExtractJinjaTemplateFromJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return FindJinjaTemplate(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindJinjaTemplate(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("jinjaPromptTemplate", out var jinja) &&
                jinja.ValueKind == JsonValueKind.Object &&
                jinja.TryGetProperty("template", out var template) &&
                template.ValueKind == JsonValueKind.String)
            {
                return template.GetString();
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindJinjaTemplate(property.Value);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindJinjaTemplate(item);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? ExtractYamlLiteralBlock(string path, string key)
    {
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (!trimmed.StartsWith(key + ":", StringComparison.Ordinal) || !trimmed.Contains('|', StringComparison.Ordinal))
            {
                continue;
            }

            var block = new List<string>();
            for (var lineIndex = index + 1; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                if (line.Trim().Length > 0 && CountLeadingSpaces(line) <= CountLeadingSpaces(lines[index]))
                {
                    break;
                }

                block.Add(line);
            }

            while (block.Count > 0 && block[0].Trim().Length == 0)
            {
                block.RemoveAt(0);
            }

            var indent = block.Where(line => line.Trim().Length > 0)
                .Select(CountLeadingSpaces)
                .DefaultIfEmpty(0)
                .Min();
            return string.Join(Environment.NewLine, block.Select(line => line.Length >= indent ? line[indent..] : ""));
        }

        return null;
    }

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string? TryReadGgufChatTemplate(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "GGUF")
            {
                return null;
            }

            _ = reader.ReadUInt32();
            _ = reader.ReadUInt64();
            var metadataCount = reader.ReadUInt64();
            for (ulong index = 0; index < metadataCount; index++)
            {
                var key = ReadGgufString(reader);
                var valueType = reader.ReadUInt32();
                if (key == "tokenizer.chat_template" && valueType == 8)
                {
                    return ReadGgufString(reader);
                }

                SkipGgufValue(reader, valueType);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string ReadGgufString(BinaryReader reader)
    {
        var length = reader.ReadUInt64();
        return Encoding.UTF8.GetString(reader.ReadBytes(checked((int)length)));
    }

    private static void SkipGgufValue(BinaryReader reader, uint valueType)
    {
        switch (valueType)
        {
            case 0:
            case 1:
            case 7:
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                return;
            case 2:
            case 3:
                reader.BaseStream.Seek(2, SeekOrigin.Current);
                return;
            case 4:
            case 5:
            case 6:
                reader.BaseStream.Seek(4, SeekOrigin.Current);
                return;
            case 8:
                reader.BaseStream.Seek(checked((long)reader.ReadUInt64()), SeekOrigin.Current);
                return;
            case 10:
            case 11:
            case 12:
                reader.BaseStream.Seek(8, SeekOrigin.Current);
                return;
            case 9:
                var elementType = reader.ReadUInt32();
                var count = reader.ReadUInt64();
                for (ulong index = 0; index < count; index++)
                {
                    SkipGgufValue(reader, elementType);
                }

                return;
            default:
                throw new InvalidDataException($"지원하지 않는 GGUF metadata value type: {valueType}");
        }
    }

    private static string BuildDisplayPatchName(string patchName)
    {
        var displayName = Regex.Replace(patchName, @"(?i)([-_.])gguf(?=$|[-_.])", "");
        return string.IsNullOrWhiteSpace(displayName) ? patchName : displayName;
    }

    private static string IndentBlock(string value, int spaces)
    {
        var indent = new string(' ', spaces);
        return string.Join(Environment.NewLine, value.Replace("\r\n", "\n").Split('\n').Select(line => indent + line));
    }

    private sealed record HubModelRecord(
        string ModelKey,
        string Publisher,
        string PatchName,
        string Directory,
        List<string> BaseModelKeys,
        List<string> SourceRepos,
        bool HasModelYaml,
        bool HasPatchMetadata,
        string MatchReason);

    private sealed record PromptTemplateSource(string Template, string Source, bool IsFallback);

    private sealed class NdxPatchMetadata
    {
        public int Version { get; init; } = 1;
        public string? CreatedAt { get; init; }
        public string? FolderName { get; init; }
        public string? Publisher { get; init; }
        public string? BaseModelKey { get; init; }
        public string? AliasModelKey { get; init; }
        public string? OutputFileName { get; init; }
        public bool OriginalModelYamlExisted { get; init; }
        public string? OriginalModelYamlSha256 { get; init; }
        public string? BackupFileName { get; init; }
    }
}
