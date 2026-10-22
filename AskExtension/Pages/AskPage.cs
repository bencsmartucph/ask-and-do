// The Ask page: type a question, Enter streams the answer into the details pane.
// The search box is the input; past turns stay listed below (newest first) and are
// sent back as history. Slash commands switch provider/model. Layout and the
// multi-line compose form follow LioQing's LLM extension (MIT).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AskExtension;

internal sealed partial class AskPage : DynamicListPage
{
    private readonly List<Turn> _turns = [];   // newest first
    private AskConfig _cfg = AskConfig.Load();   // re-read before each question, so edits to config.json apply at once
    private readonly Lock _lock = new();
    private IListItem[] _items = [];

    public AskPage()
    {
        Icon = new IconInfo("");
        Id = "bensmart.ask";              // fixed, so Command Palette hotkeys can point at it
        Title = "Ask";
        Name = "Open";
        ShowDetails = true;
        Refresh();
    }

    public override void UpdateSearchText(string oldSearch, string newSearch) => Refresh();

    public override IListItem[] GetItems()
    {
        lock (_lock)
        {
            return _items;
        }
    }

    private void Refresh()
    {
        PlaceholderText = $"Ask {_cfg.Label}   ( /gemini  /claude  /model  /clear )";
        var items = new List<IListItem>();
        var search = SearchText ?? "";

        if (search.StartsWith('/'))
        {
            items.AddRange(SlashItems(search));
        }
        else
        {
            if (!IsLoading && !string.IsNullOrWhiteSpace(search))
            {
                items.Add(new ListItem(new AnonymousCommand(() => _ = SendAsync(search)) { Name = "Send", Result = CommandResult.KeepOpen() })
                {
                    Title = search,
                    Subtitle = $"Enter to ask {_cfg.Label}",
                    Icon = new IconInfo(""),
                });
            }

            if (!IsLoading && !string.IsNullOrWhiteSpace(search))
            {
                // The search box is single-line; long or multi-line text comes in via the clipboard (#19).
                items.Add(new ListItem(new AnonymousCommand(() => _ = SendAsync(search, withClipboard: true)) { Name = "Send", Result = CommandResult.KeepOpen() })
                {
                    Title = search,
                    Subtitle = "Ask with the clipboard text attached",
                    Icon = new IconInfo(""),
                });
            }

            lock (_lock)
            {
                items.AddRange(_turns.Select(TurnItem));
            }

            if (!IsLoading && string.IsNullOrWhiteSpace(search))
            {
                // Empty box: send the clipboard on its own ("what's this?", an error, a paragraph). Kept
                // below the history so a stray Enter on an empty box doesn't send it.
                items.Add(new ListItem(new AnonymousCommand(() => _ = SendAsync("", withClipboard: true)) { Name = "Send", Result = CommandResult.KeepOpen() })
                {
                    Title = "Ask about the clipboard text",
                    Subtitle = "Sends what you copied, with no question",
                    Icon = new IconInfo(""),
                });
            }

            if (!IsLoading)
            {
                items.Add(new ListItem(new ComposePage(text => _ = SendAsync(text)))
                {
                    Title = "Compose multi-line…",
                    Subtitle = "Paste or type a longer message",
                    Icon = new IconInfo(""),
                });
            }
        }

        lock (_lock)
        {
            _items = [.. items];
        }

        RaiseItemsChanged();
    }

    private ListItem TurnItem(Turn t)
    {
        var answer = t.Assistant.Length > 0 ? t.Assistant : (t.Done ? "(no answer)" : "…");
        var timing = t.Done ? $"{t.FirstMs / 1000:0.0}s → {t.TotalMs / 1000:0.0}s" : "streaming";
        return new ListItem(new CopyTextCommand(t.Assistant) { Name = "Copy answer" })
        {
            Title = t.Question.ReplaceLineEndings(" "),
            Subtitle = answer.ReplaceLineEndings(" "),
            Icon = new IconInfo(t.Failed ? "⚠" : ""),
            Tags = t.WithClipboard ? [new Tag(t.Label), new Tag(timing), new Tag("clipboard")] : [new Tag(t.Label), new Tag(timing)],
            Details = new Details { Title = t.Question, Body = answer },
            MoreCommands =
            [
                new CommandContextItem(new MarkdownPage(t.Question, answer)) { Title = "Open full answer" },
                new CommandContextItem(new CopyTextCommand(t.Question) { Name = "Copy question" }),
            ],
        };
    }

    /// <summary>Asks straight away; the palette-home fallback (#18) calls this before opening the page.</summary>
    internal void AskNow(string text) => _ = SendAsync(text);

    private async Task SendAsync(string text, bool withClipboard = false)
    {
        text = text.Trim();
        if (IsLoading || (text.Length == 0 && !withClipboard))
        {
            return;
        }

        var question = text.Length > 0 ? text : "(clipboard)";
        if (withClipboard)
        {
            var clip = ClipboardText.Get();
            if (string.IsNullOrWhiteSpace(clip))
            {
                Toast("The clipboard has no text");
                return;
            }

            text = text.Length > 0 ? $"{text}\n\n<clipboard>\n{clip}\n</clipboard>" : $"<clipboard>\n{clip}\n</clipboard>";
        }

        _cfg = AskConfig.Load();
        List<Turn> history;
        var turn = new Turn { User = text, Question = question, WithClipboard = withClipboard, Label = _cfg.Label };
        lock (_lock)
        {
            history = _turns.Where(t => t.Done && !t.Failed).Take(_cfg.History).Reverse().ToList();
            _turns.Insert(0, turn);
        }

        IsLoading = true;
        SearchText = "";
        OnPropertyChanged(nameof(SearchText));
        Refresh();

        var sw = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in Llm.Stream(_cfg, history, text, CancellationToken.None))
            {
                if (turn.FirstMs == 0)
                {
                    turn.FirstMs = sw.Elapsed.TotalMilliseconds;
                }

                turn.Assistant += chunk;
                Refresh();
            }
        }
        catch (Exception ex)
        {
            turn.Failed = true;
            turn.Assistant += (turn.Assistant.Length > 0 ? "\n\n" : "") + "**Error:** " + ex.Message;
            Toast(ex.Message);
        }
        finally
        {
            turn.TotalMs = sw.Elapsed.TotalMilliseconds;
            turn.Done = true;
            IsLoading = false;
            Refresh();
        }
    }

    private IEnumerable<IListItem> SlashItems(string search)
    {
        var word = search[1..].Split(' ', 2)[0];
        var arg = search.Contains(' ') ? search[(search.IndexOf(' ') + 1)..].Trim() : "";

        (string Key, string Title, string Subtitle, Action Run)[] all =
        [
            ("gemini", "/gemini", $"Use Gemini ({_cfg.GeminiModel})", () => SetProvider("gemini")),
            ("claude", "/claude", $"Use Claude ({_cfg.ClaudeModel})", () => SetProvider("claude")),
            ("model", "/model <id>", $"Set the {(_cfg.IsClaude ? "Claude" : "Gemini")} model (now {_cfg.Model})", () => SetModel(arg)),
            ("clear", "/clear", $"Forget this conversation ({_turns.Count} turns)", Clear),
        ];

        foreach (var c in all.OrderByDescending(c => c.Key.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
        {
            yield return new ListItem(new AnonymousCommand(() => { c.Run(); ClearSearch(); }) { Name = "Run", Result = CommandResult.KeepOpen() })
            {
                Title = c.Title,
                Subtitle = c.Subtitle,
                Icon = new IconInfo(""),
            };
        }

        yield return new ListItem(new OpenUrlCommand(AskConfig.Dir) { Name = "Open folder" })
        {
            Title = "/config",
            Subtitle = "Open the folder with config.json (system prompt, history length, models)",
            Icon = new IconInfo(""),
        };
    }

    private void SetProvider(string provider)
    {
        _cfg = AskConfig.Load();
        _cfg.Provider = provider;
        _cfg.Save();
        Toast($"Now asking {_cfg.Label}", MessageState.Success);
    }

    private void SetModel(string model)
    {
        if (model.Length == 0)
        {
            Toast("Type the model id after /model");
            return;
        }

        _cfg = AskConfig.Load();
        if (_cfg.IsClaude)
        {
            _cfg.ClaudeModel = model;
        }
        else
        {
            _cfg.GeminiModel = model;
        }

        _cfg.Save();
        Toast($"Now asking {_cfg.Label}", MessageState.Success);
    }

    private void Clear()
    {
        lock (_lock)
        {
            _turns.Clear();
        }
    }

    private void ClearSearch()
    {
        SearchText = "";
        OnPropertyChanged(nameof(SearchText));
        Refresh();
    }

    private static void Toast(string message, MessageState state = MessageState.Error) =>
        new ToastStatusMessage(new StatusMessage { Message = message, State = state }) { Duration = 6000 }.Show();
}

internal sealed partial class MarkdownPage : ContentPage
{
    private readonly string _body;

    public MarkdownPage(string title, string body)
    {
        Title = title;
        Name = "Open";
        _body = body;
    }

    public override IContent[] GetContent() => [new MarkdownContent(_body)];
}

internal sealed partial class ComposePage : ContentPage
{
    private readonly ComposeForm _form;

    public ComposePage(Action<string> send)
    {
        Title = "Compose";
        Name = "Compose";
        Icon = new IconInfo("");
        _form = new ComposeForm(send);
    }

    public override IContent[] GetContent() => [_form];
}

internal sealed partial class ComposeForm : FormContent
{
    private readonly Action<string> _send;

    public ComposeForm(Action<string> send)
    {
        _send = send;
        TemplateJson = """
        {
            "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
            "type": "AdaptiveCard",
            "version": "1.6",
            "body": [
                { "type": "Input.Text", "id": "message", "placeholder": "Paste or type here…",
                  "isMultiline": true, "isRequired": true, "errorMessage": "Empty" }
            ],
            "actions": [ { "type": "Action.Submit", "title": "Send" } ]
        }
        """;
    }

    public override CommandResult SubmitForm(string payload)
    {
        var text = (string?)JsonNode.Parse(payload)?["message"];
        if (string.IsNullOrWhiteSpace(text))
        {
            return CommandResult.KeepOpen();
        }

        _send(text);
        return CommandResult.GoBack();
    }
}
