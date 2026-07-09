# LM Studio Think Patcher

Windows Forms utility for scanning a local LM Studio model root and writing
non-thinking prompt-template overrides for Codex-style Responses API calls.

## Features

- Remembers the selected model root and suffix.
- Lists model folders under the selected root. Multipart GGUF shards in one folder are one model.
- Shows patch status and patched model name.
- Allows editing each patched model name before repatching.
- Supports patching every model or only unpatched models.
- Uses `%USERPROFILE%\.lmstudio\hub\models\<publisher>\<patched-name>\model.yaml` and `ndx-model-patch.json` as the patch source of truth.

## Build

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

The published app is written to `publish/`. Re-run the same command after
updates; it reuses that folder instead of creating versioned publish folders.
Transient `bin/` and `obj/` build folders are removed automatically after
publish completes.
