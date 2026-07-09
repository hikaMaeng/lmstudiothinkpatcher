using System.Text.RegularExpressions;

namespace LmStudioThinkPatcher;

static class TemplateCatalog
{
    private const string LowEffortGuard = "{%- set ndx_low_effort = reasoning_effort is defined and reasoning_effort in ['none', 'minimal', 'low'] -%}\n{%- if ndx_low_effort -%}{%- set enable_thinking = false -%}{%- set thinking_mode = 'disabled' -%}{%- endif -%}\n";

    public static bool SupportsReasoningPatch(string template)
    {
        return template.Contains("reasoning_effort", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("enable_thinking", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("thinking_mode", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("<think", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("</think", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("<mm:think", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("</mm:think", StringComparison.OrdinalIgnoreCase);
    }

    public static string PatchForReasoningEffortLow(string template)
    {
        var next = template.Replace("| safe", "", StringComparison.Ordinal);
        next = next.Replace("reasoning_effort is defined and reasoning_effort == 'low'", "reasoning_effort is defined and reasoning_effort in ['none', 'minimal', 'low']", StringComparison.Ordinal);
        next = next.Replace("reasoning_effort != 'low'", "reasoning_effort not in ['none', 'minimal', 'low']", StringComparison.Ordinal);
        next = next.Replace("{%- set ns_flags = namespace(enable_thinking=true) %}", "{%- set ns_flags = namespace(enable_thinking=false) %}", StringComparison.Ordinal);
        next = next.Replace("{%- set preserve_thinking = preserve_thinking | default(true) %}", "{%- set preserve_thinking = preserve_thinking | default(false) %}", StringComparison.Ordinal);
        next = next.Replace("{{- '<|im_start|>assistant\\n<think>\\n' }}", "{%- if ndx_low_effort -%}{{- '<|im_start|>assistant\\n' }}{%- else -%}{{- '<|im_start|>assistant\\n<think>\\n' }}{%- endif -%}", StringComparison.Ordinal);
        next = next.Replace("{{- '<|im_start|>assistant\\n<think></think>' }}", "{{- '<|im_start|>assistant\\n' }}", StringComparison.Ordinal);
        return next.StartsWith("{%- set ndx_low_effort =", StringComparison.Ordinal) ? next : LowEffortGuard + next;
    }

    public static (string Start, string End) GetReasoningMarkers(string template)
    {
        var start = ExtractJinjaStringAssignment(template, "think_begin_token") ??
            ExtractJinjaStringAssignment(template, "think_start_token");
        var end = ExtractJinjaStringAssignment(template, "think_end_token");
        if (!string.IsNullOrWhiteSpace(start) && !string.IsNullOrWhiteSpace(end))
        {
            return (start, end);
        }

        if (template.Contains("<mm:think>", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("</mm:think>", StringComparison.OrdinalIgnoreCase))
        {
            return ("<mm:think>", "</mm:think>");
        }

        if (template.Contains("<thinking>", StringComparison.OrdinalIgnoreCase) ||
            template.Contains("</thinking>", StringComparison.OrdinalIgnoreCase))
        {
            return ("<thinking>", "</thinking>");
        }

        return ("<think>", "</think>");
    }

    private static string? ExtractJinjaStringAssignment(string template, string variableName)
    {
        var match = Regex.Match(
            template,
            @"set\s+" + Regex.Escape(variableName) + @"\s*=\s*(['""])(?<value>.*?)\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value : null;
    }

    public const string FallbackTemplate = """
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
{{- '<|im_start|>user\n<tool_response>\n' + render_content(message.content) + '\n</tool_response><|im_end|>\n' -}}
{%- elif role in ['system', 'user', 'assistant'] -%}
{{- '<|im_start|>' + role + '\n' + render_content(message.content) + '<|im_end|>\n' -}}
{%- endif -%}
{%- endfor -%}
{%- if add_generation_prompt -%}
{%- if enable_thinking is defined and enable_thinking -%}
{{- '<|im_start|>assistant\n<think>\n' -}}
{%- else -%}
{{- '<|im_start|>assistant\n' -}}
{%- endif -%}
{%- endif -%}
""";
}
