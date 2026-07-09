# LM Studio Think Patcher

LM Studio Think Patcher creates patched LM Studio model aliases whose Jinja
prompt templates can turn thinking/reasoning behavior on or off for models that
were not originally exposed with that control.

It is especially useful for Codex-style OpenAI Responses API workflows. LM
Studio documents OpenAI-compatible endpoints, including `POST /v1/responses`,
and its Responses example includes a `reasoning` object with an `effort` value.
OpenAI's own Responses API documents `reasoning.effort` as the knob that guides
how much reasoning-capable models should think. Local models, however, often
express thinking behavior in their own Jinja chat templates using markers and
variables such as `<think>...</think>`, `enable_thinking`,
`reasoning_effort`, or similar names.

This tool bridges that practical gap on the model side: it scans downloaded
GGUF models, reads their embedded or configured chat templates, and writes LM
Studio `model.yaml` aliases that add a controllable `enableThinking` custom
field plus reasoning parser settings.

Instead of hand-editing every model folder every time LM Studio or a model is
updated, you can scan once, patch all compatible models, and keep the generated
aliases under `~/.lmstudio/hub/models`.

## Why This Exists

LM Studio recognizes models from two related locations:

- Raw downloaded model files usually live under
  `~/.lmstudio/models/<publisher>/<model-folder>/`.
- Hub model definitions live under
  `~/.lmstudio/hub/models/<publisher>/<model-name>/model.yaml`.

The raw model folder contains the actual GGUF files. The hub `model.yaml` is the
metadata and configuration layer LM Studio uses to present a model alias,
associate it with a base model, set compatibility metadata, define model-specific
custom fields, and bake in runtime configuration such as inference parameters
and prompt-template overrides.

That separation is what makes this tool possible. It does not rewrite the GGUF
weights. Instead, it creates or updates a hub model alias with a generated
`model.yaml`. The alias points back to the original base model while replacing
the prompt-template configuration with a safer, controllable version.

## What Gets Patched

For each compatible GGUF model, the tool writes:

- `model.yaml`: the LM Studio hub model definition and prompt-template override.
- `ndx-model-patch.json`: metadata recording when the patch was created, the
  base model key, the alias model key, and backup information.
- `model.yaml.ndx-backup.<timestamp>`: a backup when an existing `model.yaml`
  is replaced.

Generated model aliases are written to:

```text
~/.lmstudio/hub/models/<publisher>/<patched-name>/model.yaml
```

By default, new aliases use the `-ndx` suffix, for example:

```text
unsloth/Qwen3.6-27B-MTP-GGUF
unsloth/Qwen3.6-27B-MTP-GGUF-ndx
```

Existing LM Studio aliases can also be repatched in place when they already
match the base model.

## How LM Studio Uses `model.yaml`

The generated `model.yaml` acts as a lightweight model wrapper. It declares:

- `model`: the alias key LM Studio should expose.
- `base`: the original model key and source information.
- `metadataOverrides`: presentation and capability metadata such as GGUF
  compatibility and `reasoning: true`.
- `customFields`: user-controllable fields LM Studio can bind into the prompt
  template.
- `config.operation.fields`: runtime configuration overrides, including the
  Jinja prompt template and reasoning parser markers.

LM Studio's model.yaml documentation describes `base` as pointing to concrete
model files, `config.operation.fields` as inference-time settings, and
`customFields` as model-specific fields whose effects can set Jinja variables.
The important part is that LM Studio can expose the same GGUF model through a
different alias and configuration. That lets the patched model behave like the
original model while gaining explicit control over the prompt template's
thinking switch.

## Jinja Template Optimization

Many reasoning models ship with Jinja templates that decide when to open a
thinking channel. LM Studio's prompt-template documentation notes that it can
automatically configure prompt templates from model metadata and that templates
can be expressed in Jinja. The exact variable names differ by model family, so
this tool looks for templates in this order:

1. Existing hub `model.yaml` prompt templates.
2. LM Studio internal per-model configuration files.
3. The GGUF `tokenizer.chat_template` metadata.

It then checks whether the template appears to support reasoning control by
looking for known markers and variables such as:

- `reasoning_effort`
- `enable_thinking`
- `thinking_mode`
- `reasoning_content`
- `<think>` / `</think>`
- `<mm:think>` / `</mm:think>`

When a compatible template is found, the patcher generates a prompt-template
override that defaults thinking to off while still allowing LM Studio to set the
variable explicitly. It also removes Jinja `| safe` filters from the copied
template because those filters are unnecessary in this generated YAML context
and can make templates less portable across runtimes.

The generated alias adds this custom field:

```yaml
customFields:
  - key: enableThinking
    displayName: Enable Thinking
    type: boolean
    defaultValue: false
    effects:
      - type: setJinjaVariable
        variable: enable_thinking
```

That field follows LM Studio's documented `customFields` pattern: a field named
`enableThinking` applies a `setJinjaVariable` effect to the Jinja variable
`enable_thinking`. The result is a model alias where thinking can be disabled by
default and enabled intentionally through LM Studio's model configuration layer.

## Responses API Reasoning Behavior

Codex-style tools often call a local LM Studio server through an
OpenAI-compatible Responses API shape. LM Studio's docs state that Codex can
talk to LM Studio through the OpenAI-compatible `POST /v1/responses` endpoint.
The LM Studio Responses example shows:

```json
{
  "model": "openai/gpt-oss-20b",
  "input": "Provide a prime number less than 50",
  "reasoning": { "effort": "low" }
}
```

OpenAI's current Responses API reference describes `reasoning.effort` as a
model-dependent setting for reasoning models, with values such as `none`,
`minimal`, `low`, `medium`, `high`, and `xhigh`. Lower effort favors latency and
token use; higher effort gives the model more room to reason.

The problem is that a local GGUF model's original Jinja template may not expose
that intent in a way LM Studio can control cleanly. Some templates always open a
thinking block, some key off `enable_thinking`, and others use model-specific
markers. This patcher does not change LM Studio's server API. Instead, it makes
the selected model alias more compatible with reasoning-aware clients by adding
the model-side pieces those clients need:

The patched `model.yaml` bridges that gap:

- It declares the model as reasoning-capable.
- It installs parser settings for the model's reasoning start/end markers.
- It exposes `enableThinking` as a documented model.yaml custom field.
- It rewrites the prompt template so thinking is controlled by the field instead
  of being permanently forced on by the original template.

In practice, this makes patched aliases much easier to use from clients and
workflows that expect reasoning-capable local models. You get a predictable
non-thinking default for normal responses, while preserving a model-level path
to enable thinking when the runtime configuration should allow it.

## Why Use The Tool Instead Of Editing By Hand

Doing this manually is fragile:

- Every model family uses slightly different Jinja variables and thinking
  markers.
- Multipart GGUF models need to be treated as one logical model.
- Existing LM Studio hub aliases may need to be detected and backed up.
- A small YAML indentation or template escaping mistake can make an alias fail
  to load.
- Repeating the same edits after model updates is tedious and easy to get wrong.

LM Studio Think Patcher automates those repetitive details. It scans your model
root, detects compatible templates, creates the correct hub alias structure,
backs up existing YAML files, records patch metadata, and lets you repatch all
models consistently.

## Documentation Basis

This README is based on the public documentation available on 2026-07-09:

- [LM Studio: Introduction to model.yaml](https://lmstudio.ai/docs/app/modelyaml)
  documents `model`, `base`, `metadataOverrides`, `config`, and `customFields`,
  including the `enableThinking` / `setJinjaVariable` pattern.
- [LM Studio: Prompt Template](https://lmstudio.ai/docs/app/advanced/prompt-template)
  documents automatic prompt-template configuration and Jinja prompt templates.
- [LM Studio: OpenAI Compatibility Endpoints](https://lmstudio.ai/docs/developer/openai-compat)
  documents `POST /v1/responses` as an OpenAI-compatible endpoint.
- [LM Studio: Responses](https://lmstudio.ai/docs/developer/openai-compat/responses)
  shows a local `/v1/responses` request with `reasoning: { "effort": "low" }`.
- [LM Studio: Codex integration](https://lmstudio.ai/docs/integrations/codex)
  states that Codex can talk to LM Studio through the OpenAI-compatible
  Responses endpoint.
- [OpenAI: Create a model response](https://platform.openai.com/docs/api-reference/responses/create)
  documents the `reasoning` object and `reasoning.effort` values for reasoning
  models.
- [OpenAI: Reasoning models](https://platform.openai.com/docs/guides/reasoning)
  explains that `reasoning.effort` guides how much a model thinks, with lower
  effort favoring speed and token savings and higher effort favoring more
  complete reasoning.

## Features

- Remembers the selected model root and suffix.
- Lists model folders under the selected root. Multipart GGUF shards in one folder are one model.
- Shows patch status and patched model name.
- Allows editing each patched model name before repatching.
- Supports patching every model or only unpatched models.
- Uses `%USERPROFILE%\.lmstudio\hub\models\<publisher>\<patched-name>\model.yaml` and `ndx-model-patch.json` as the patch source of truth.
- Provides a CLI patch mode for applying patches without opening the GUI.

## CLI Usage

Scan a model root:

```bash
dotnet run -f net10.0 -- --scan ~/.lmstudio/models -ndx
```

Patch every compatible model:

```bash
dotnet run -f net10.0 -- --patch ~/.lmstudio/models -ndx
```

Models without reasoning-capable prompt templates are skipped. Embedding models,
for example, are normally not patch targets.

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
