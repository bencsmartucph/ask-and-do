// Providers for the Ask page: Gemini over raw HTTP (SSE), Claude through the
// official Anthropic SDK. Keys are read where they already live on this machine,
// never written by this extension:
//   Gemini  GEMINI_API_KEY (env or HKCU\Environment), else the LioQing LLM
//           extension's ~/.config/LlmExtensionForCmdPal/config.json "ApiKey"
//   Claude  AHK_ANTHROPIC_KEY (env or HKCU\Environment) -- a private name, so
//           stray SDK runs elsewhere don't bill the API by accident
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Win32;

namespace AskExtension;

internal sealed class Turn
{
    public string User { get; set; } = "";          // what was sent, clipboard included; history replays this
    public string Question { get; set; } = "";      // what you typed; shown in the list
    public bool WithClipboard { get; set; }
    public string Assistant { get; set; } = "";
    public string Label { get; set; } = "";
    public double FirstMs { get; set; }
    public double TotalMs { get; set; }
    public bool Done { get; set; }
    public bool Failed { get; set; }
}

internal sealed class AskConfig
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "cmdpal-ask");
    private static readonly string FilePath = Path.Combine(Dir, "config.json");

    public string Provider { get; set; } = "gemini";            // gemini | claude
    public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";
    public string ClaudeModel { get; set; } = "claude-haiku-5-5";
    public string System { get; set; } =
        "You are an AI assistant, you should provide help to the user with their query. " +
        "Be super concise/Direct: Just give the answers, no small talk, minimal words, straight to the point.";
    public int History { get; set; } = 6;                       // past turns sent back
    public int MaxTokens { get; set; } = 4096;
    public string DoModel { get; set; } = "sonnet";             // claude -p --model
    public string DoDir { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public bool IsClaude => Provider == "claude";
    public string Model => IsClaude ? ClaudeModel : GeminiModel;
    public string Label => (IsClaude ? "Claude" : "Gemini") + " · " + Model;

    public static AskConfig Load()
    {
        var c = new AskConfig();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject j)
            {
                c.Provider = (string?)j["Provider"] ?? c.Provider;
                c.GeminiModel = (string?)j["GeminiModel"] ?? c.GeminiModel;
                c.ClaudeModel = (string?)j["ClaudeModel"] ?? c.ClaudeModel;
                c.System = (string?)j["System"] ?? c.System;
                c.History = (int?)j["History"] ?? c.History;
                c.MaxTokens = (int?)j["MaxTokens"] ?? c.MaxTokens;
                c.DoModel = (string?)j["DoModel"] ?? c.DoModel;
                c.DoDir = (string?)j["DoDir"] ?? c.DoDir;
            }
        }
        catch (Exception)
        {
            c.Save();   // missing or unreadable: write the defaults so there is a file to edit
        }

        return c;
    }

    public void Save()
    {
        var j = new JsonObject
        {
            ["Provider"] = Provider,
            ["GeminiModel"] = GeminiModel,
            ["ClaudeModel"] = ClaudeModel,
            ["System"] = System,
            ["History"] = History,
            ["MaxTokens"] = MaxTokens,
            ["DoModel"] = DoModel,
            ["DoDir"] = DoDir,
        };
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, j.ToJsonString(new() { WriteIndented = true }));
    }
}

internal static class Llm
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>Streams the answer to <paramref name="user"/>; history is oldest first.</summary>
    public static IAsyncEnumerable<string> Stream(AskConfig cfg, IReadOnlyList<Turn> history, string user, CancellationToken ct) =>
        cfg.IsClaude ? Claude(cfg, history, user, ct) : Gemini(cfg, history, user, ct);

    private static async IAsyncEnumerable<string> Gemini(
        AskConfig cfg, IReadOnlyList<Turn> history, string user, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = GeminiKey() ?? throw new InvalidOperationException(
            "No Gemini key: set GEMINI_API_KEY, or keep the LLM Chat extension's config.json");

        var contents = new JsonArray();
        foreach (var t in history)
        {
            contents.Add(Part("user", t.User));
            contents.Add(Part("model", t.Assistant));
        }

        contents.Add(Part("user", user));
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = cfg.System }) },
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = cfg.MaxTokens },
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{cfg.GeminiModel}:streamGenerateContent?alt=sse";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-goog-api-key", key);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Gemini {(int)resp.StatusCode}: {ErrorText(err)}");
        }

        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = JsonNode.Parse(line[5..])?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
            if (parts == null)
            {
                continue;
            }

            foreach (var p in parts)
            {
                if ((bool?)p?["thought"] == true)
                {
                    continue;
                }

                var text = (string?)p?["text"];
                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                }
            }
        }
    }

    private static async IAsyncEnumerable<string> Claude(
        AskConfig cfg, IReadOnlyList<Turn> history, string user, [EnumeratorCancellation] CancellationToken ct)
    {
        var key = EnvKey("AHK_ANTHROPIC_KEY") ?? throw new InvalidOperationException("No Claude key: AHK_ANTHROPIC_KEY is not set");
        var client = new AnthropicClient { ApiKey = key };

        var messages = new List<MessageParam>();
        foreach (var t in history)
        {
            messages.Add(new() { Role = Role.User, Content = t.User });
            messages.Add(new() { Role = Role.Assistant, Content = t.Assistant });
        }

        messages.Add(new() { Role = Role.User, Content = user });

        var parameters = new MessageCreateParams
        {
            Model = cfg.ClaudeModel,
            MaxTokens = cfg.MaxTokens,
            System = cfg.System,
            Messages = messages,
            // Quick answers: no thinking, low effort -> shortest time to first token.
            // Haiku 5.5 accepts disabled thinking at effort high or below.
            Thinking = new ThinkingConfigDisabled(),
            OutputConfig = new OutputConfig { Effort = Effort.Low },
        };

        await foreach (var ev in client.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
            {
                yield return text.Text;
            }
        }
    }

    private static JsonObject Part(string role, string text) => new()
    {
        ["role"] = role,
        ["parts"] = new JsonArray(new JsonObject { ["text"] = text }),
    };

    private static string ErrorText(string body)
    {
        try
        {
            return (string?)JsonNode.Parse(body)?["error"]?["message"] ?? body;
        }
        catch (Exception)
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    private static string? EnvKey(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
        {
            v = Registry.GetValue(@"HKEY_CURRENT_USER\Environment", name, null) as string;
        }

        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string? GeminiKey()
    {
        var key = EnvKey("GEMINI_API_KEY");
        if (key != null)
        {
            return key;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "LlmExtensionForCmdPal", "config.json");
            var k = (string?)JsonNode.Parse(File.ReadAllText(path))?["ApiKey"];
            return string.IsNullOrWhiteSpace(k) ? null : k;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
