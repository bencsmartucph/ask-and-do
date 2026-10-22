// Runs headless Claude Code (`claude -p`) for the Do page and streams what it does.
// Same rules as the author's other headless Claude Code launchers: pinned CLI path,
// ANTHROPIC_API_KEY stripped so the run stays on the subscription login, MCP servers
// skipped (--strict-mcp-config cuts start-up from ~15 s to ~3 s).
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AskExtension;

internal sealed record AgentResult(string Text, string? SessionId, bool IsError);

internal static class Agent
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Cli = Path.Combine(Home, ".local", "bin", "claude.exe");

    // Do's whole instruction set: a short map of where things live. Copied next to the
    // exe at build time (see AskExtension.csproj). Settings sources are off, so no
    // CLAUDE.md, hooks or user settings load: lighter and faster, and nothing else steers it.
    private static readonly string PromptFile = Path.Combine(AppContext.BaseDirectory, "do-prompt.md");

    /// <param name="readOnly">Plan mode: Claude may read and answer but not edit or run
    /// changing commands. Used whenever clipboard text (untrusted) is in the session.</param>
    public static async Task<AgentResult> RunAsync(
        string prompt, string? resume, bool readOnly, AskConfig cfg, Action<string> onStep, Action<string> onSession, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Cli)
        {
            WorkingDirectory = cfg.DoDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[]
        {
            "-p", prompt, "--model", cfg.DoModel, "--permission-mode", readOnly ? "plan" : "auto", "--strict-mcp-config",
            "--output-format", "stream-json", "--verbose", "--setting-sources", "", "--append-system-prompt-file", PromptFile,
        })
        {
            psi.ArgumentList.Add(a);
        }

        if (resume != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(resume);
        }

        psi.Environment.Remove("ANTHROPIC_API_KEY");
        var bin = Path.Combine(Home, "bin");                     // es.exe lives here
        var path = psi.Environment.TryGetValue("PATH", out var p) ? p ?? "" : "";
        if (!path.Contains(bin, StringComparison.OrdinalIgnoreCase))
        {
            psi.Environment["PATH"] = bin + ";" + path;
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("claude.exe did not start");
        proc.StandardInput.Close();
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var reg = ct.Register(() => Kill(proc));

        AgentResult? result = null;
        while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            JsonNode? j;
            try
            {
                j = JsonNode.Parse(line);
            }
            catch (Exception)
            {
                continue;
            }

            var type = (string?)j?["type"];
            if (type == "system" && (string?)j?["subtype"] == "init" && (string?)j?["session_id"] is { } sid)
            {
                onSession(sid);
            }
            else if (type == "assistant" && j?["message"]?["content"] is JsonArray content)
            {
                foreach (var block in content)
                {
                    if ((string?)block?["type"] == "tool_use")
                    {
                        onStep($"`{(string?)block?["name"]}` {Describe(block?["input"])}");
                    }
                    else if ((string?)block?["type"] == "text" && (string?)block?["text"] is { Length: > 0 } text)
                    {
                        onStep(text.Trim());
                    }
                }
            }
            else if (type == "result")
            {
                result = new AgentResult(
                    (string?)j?["result"] ?? (string?)j?["subtype"] ?? "(no result)",
                    (string?)j?["session_id"],
                    (bool?)j?["is_error"] == true);
                break;                                           // the CLI lingers a second or two after this
            }
        }

        if (!proc.WaitForExit(3000))
        {
            Kill(proc);
        }

        if (result != null)
        {
            return result;
        }

        var err = (await stderr).Trim();
        return new AgentResult(err.Length > 0 ? err : $"claude exited with code {proc.ExitCode}", null, true);
    }

    // One readable line per tool call: the command, path or pattern it was given.
    private static string Describe(JsonNode? input)
    {
        foreach (var key in new[] { "command", "file_path", "pattern", "skill", "url", "query", "description" })
        {
            if ((string?)input?[key] is { Length: > 0 } v)
            {
                v = v.ReplaceLineEndings(" ");
                return v.Length > 140 ? v[..140] + "…" : v;
            }
        }

        return "";
    }

    private static void Kill(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // already gone
        }
    }

    /// <summary>Opens the session in Windows Terminal so you can carry on interactively.</summary>
    public static void OpenInTerminal(string sessionId, AskConfig cfg)
    {
        var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var psi = new ProcessStartInfo(wt) { UseShellExecute = false };
        foreach (var a in new[] { "-d", cfg.DoDir, Cli, "--resume", sessionId })
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Remove("ANTHROPIC_API_KEY");
        Process.Start(psi);
    }
}

/// <summary>Plain-text clipboard read without WinRT (works from this background COM process).</summary>
internal static partial class ClipboardText
{
    private const uint CfUnicodeText = 13;

    public static string? Get()
    {
        for (var i = 0; i < 5; i++)                              // another app may hold it briefly
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var h = GetClipboardData(CfUnicodeText);
                    if (h == IntPtr.Zero)
                    {
                        return null;
                    }

                    var ptr = GlobalLock(h);
                    try
                    {
                        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUni(ptr);
                    }
                    finally
                    {
                        GlobalUnlock(h);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(20);
        }

        return null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr hWndNewOwner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetClipboardData(uint uFormat);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GlobalLock(IntPtr hMem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(IntPtr hMem);
}
