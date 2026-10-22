# Ask and Do

Two commands for the Windows [Command Palette](https://learn.microsoft.com/windows/powertoys/command-palette/overview) (PowerToys):

| Command | What it is |
|---|---|
| **Ask** | Quick questions to a fast model, answered in the palette's details pane. No tools. |
| **Do** | A quick task for headless Claude Code on this machine: change a setting, find a file, check something. |

Both are meant to be bound to a hotkey, so a question or a small task costs one keystroke and no window switching.

## Ask

Type the question and press Enter. The answer streams into the details pane. Earlier turns stay listed and are sent back as history, so follow-ups work; Enter on an old turn copies its answer.

- **Providers:** Gemini (`gemini-3.5-flash-lite`, the default) or Claude (`claude-haiku-5-5`, thinking off). Both start answering in about 0.6 s once warm. `/gemini` and `/claude` switch; `/model <id>` changes the current provider's model; `/clear` forgets the conversation; `/config` opens the config folder.
- **From the palette home:** type a question straight after opening the palette, and an "Ask: …" row sits at the bottom. Enter asks it. Typing alone sends nothing.
- **Multi-line:** the search box is single-line. Copy the long text, type the question and pick "Ask with the clipboard text attached", or use "Compose multi-line…".
- **Timing:** each answer is tagged with seconds to first token and in total.
- **Config:** `%USERPROFILE%\.config\cmdpal-ask\config.json`: system prompt, history length, models, Do's model and folder. It is read before every question, so edits apply without a restart.
- **Keys** are read, never written: Gemini from `GEMINI_API_KEY`, else the LLM Chat extension's `~\.config\LlmExtensionForCmdPal\config.json`; Claude from `AHK_ANTHROPIC_KEY` (environment or `HKCU\Environment`). The private name keeps other tools that read `ANTHROPIC_API_KEY` from billing the API by accident.

## Do

The task goes to `claude -p` (Sonnet by default, `--permission-mode auto`) in the folder set as `DoDir` (your user folder by default), on your Claude subscription login: `ANTHROPIC_API_KEY` is removed from its environment. A simple task takes about 12 s. It is kept light on purpose:

- `--setting-sources ""`: no CLAUDE.md, user settings or hooks. Its whole instruction set is [`do-prompt.md`](do-prompt.md), a short map of where things live on the machine, copied next to the exe at build time and passed with `--append-system-prompt-file`. Edit it to describe your machine, then redeploy.
- `--strict-mcp-config`: no MCP servers, so start-up takes about 3 s instead of 15.

Tool calls are listed under the reply. Follow-ups resume the same session; `/new` starts fresh; `/model <alias>` switches model. Ctrl+K on a finished job, then "Continue in Terminal", opens `claude --resume` in Windows Terminal.

**Clipboard text is untrusted.** "Run with the clipboard text attached" marks it as data, not instructions, and runs **read-only** (`--permission-mode plan`). A session with clipboard text in it stays read-only until `/new`, so copied text can't steer a run that edits.

The manifest turns off AppData and registry write virtualisation (`unvirtualizedResources`). `claude.exe` runs inside this package's context, and its setting changes would otherwise land in a private copy that no real app reads.

## Build and deploy

```powershell
powershell -ExecutionPolicy Bypass -File .\deploy.ps1
```

Builds with the .NET 10 SDK (no Visual Studio needed: the Windows SDK build tools and CsWinRT metadata come from NuGet and the OS, see `AskExtension.csproj`), registers the package from the build output (needs Developer Mode), and restarts Command Palette. Bind hotkeys in the palette's settings to the command IDs `bensmart.ask` and `bensmart.do`.

Code: `Pages\AskPage.cs` and `Pages\DoPage.cs` (UI), `Llm.cs` (providers, config, keys), `Agent.cs` (the `claude -p` runner and clipboard read).

## Status

In use on one Windows 11 machine since 19 October 2026. Not yet tried in the palette: "Continue in Terminal", Do's clipboard run, and `/model`. The registered build is a Debug build; a trimmed Release build would first need the `IL2026` JSON warnings in `Llm.cs` fixed.

## Uninstall

```powershell
Get-AppxPackage BenSmart.AskExtension | Remove-AppxPackage
```

## Licence

MIT, see [LICENSE](LICENSE).
