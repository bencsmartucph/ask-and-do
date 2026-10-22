// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace AskExtension;

public partial class AskExtensionCommandsProvider : CommandProvider
{
    private readonly ICommandItem[] _commands;
    private readonly IFallbackCommandItem[] _fallbacks;

    public AskExtensionCommandsProvider()
    {
        DisplayName = "Ask";
        Icon = IconHelpers.FromRelativePath("Assets\\StoreLogo.png");
        var ask = new AskPage();   // one instance: the fallback asks on the same page Win+O opens
        _commands = [
            new CommandItem(ask) { Title = DisplayName, Subtitle = "Quick questions to Gemini or Claude" },
            new CommandItem(new DoPage()) { Title = "Do", Subtitle = "Quick tasks: headless Claude Code on this machine" },
        ];
        _fallbacks = [new AskFallback(ask)];
    }

    public override ICommandItem[] TopLevelCommands()
    {
        return _commands;
    }

    public override IFallbackCommandItem[] FallbackCommands() => _fallbacks;
}

/// <summary>
/// #18: whatever is typed on the palette's home shows an "Ask: …" row at the bottom.
/// Typing only updates the title; nothing is sent until Enter, which asks on the Ask
/// page and opens it with the answer streaming.
/// </summary>
internal sealed partial class AskFallback : FallbackCommandItem
{
    private readonly AskNowCommand _command;

    public AskFallback(AskPage page)
        : this(new AskNowCommand(page))
    {
    }

    private AskFallback(AskNowCommand command)
        : base(command, "Ask", "bensmart.ask.fallback")
    {
        _command = command;
        Title = "";
        Subtitle = "Ask Gemini or Claude";
        Icon = new IconInfo("");
    }

    public override void UpdateQuery(string query)
    {
        _command.Query = query;
        Title = string.IsNullOrWhiteSpace(query) ? "" : $"Ask: {query}";
    }
}

internal sealed partial class AskNowCommand : InvokableCommand
{
    private readonly AskPage _page;

    public AskNowCommand(AskPage page)
    {
        _page = page;
        Name = "Ask";
    }

    public string Query { get; set; } = "";

    public override ICommandResult Invoke()
    {
        _page.AskNow(Query);
        return CommandResult.GoToPage(new GoToPageArgs { PageId = _page.Id, NavigationMode = NavigationMode.Push });
    }
}
