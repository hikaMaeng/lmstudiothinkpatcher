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

## Build Windows

```powershell
.\build-windows.ps1
```

The Windows app is published to `publish/windows/`. Re-run the same script after
updates; it reuses that folder instead of creating versioned publish folders.
Transient `bin/` and `obj/` build folders are removed automatically.

## Build macOS

Run this on macOS with Xcode command line tools installed:

```bash
./build-macos.sh
```

The macOS SwiftUI app is published to
`publish/macos/LM Studio Think Patcher.app`. The temporary SwiftPM `.build/`
folder is removed automatically after the app bundle is assembled.
