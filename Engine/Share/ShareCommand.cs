namespace MuxSwarm.Engine.Share;

/// <summary>
/// <c>/share</c> and <c>/join</c> slash commands. Session-agnostic (menu + every session loop),
/// like <c>/daemon</c>: sharing is process-level state that follows whatever the TUI shows.
/// </summary>
internal static class ShareCommand
{
    /// <summary>True when <paramref name="line"/> is a /share or /join command.</summary>
    public static bool Matches(string? line)
    {
        var cmd = (line ?? "").Trim().Split(' ', 2)[0].ToLowerInvariant();
        return cmd is "/share" or "/join";
    }

    /// <summary>Dispatch a /share or /join line.</summary>
    public static async Task RunAsync(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string cmd = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1] : "";
        try
        {
            if (cmd == "/join") await JoinAsync(arg);
            else await ShareAsync(arg);
        }
        finally
        {
            MuxConsole.TuiForceRedraw();
        }
    }

    private static async Task ShareAsync(string arg)
    {
        string sub = arg.Split(' ', 2)[0].ToLowerInvariant();
        string rest = arg.Contains(' ') ? arg[(arg.IndexOf(' ') + 1)..].Trim() : "";
        switch (sub)
        {
            case "status":
            case "link":
                PrintStatus();
                return;
            case "stop":
            case "off":
                if (!ShareHost.IsActive) { MuxConsole.WriteMuted("Not sharing."); return; }
                await ShareHost.StopAsync();
                MuxConsole.WriteSuccess("Sharing stopped. The link no longer works and all guests were disconnected.");
                return;
            case "kick":
                if (rest.Length == 0) { MuxConsole.WriteMuted("Usage: /share kick <name|#id>"); return; }
                MuxConsole.WriteMuted(ShareHost.Kick(rest) ? $"Removed {rest}." : $"No guest matching '{rest}'.");
                return;
            case "":
            case "start":
            case "on":
            case "--local":
            case "--lan":
                break;
            default:
                MuxConsole.WriteMuted("Usage: /share [--local|--lan] | status | stop | kick <name>");
                return;
        }

        if (ShareHost.IsActive) { PrintStatus(); return; }
        if (!MuxConsole.TuiActive || !MuxConsole.FrameEngineEnabled)
        {
            MuxConsole.WriteWarning("Sharing needs the full-frame TUI renderer (console.renderEngine = \"frame\", the default).");
            return;
        }

        bool? lan = arg.Contains("--lan", StringComparison.OrdinalIgnoreCase) ? true
            : arg.Contains("--local", StringComparison.OrdinalIgnoreCase) ? false : null;
        if (lan is null)
        {
            const string local = "This machine only (loopback)";
            const string all = "All interfaces - people on your LAN / Tailscale can join";
            const string cancel = "Cancel";
            // Loopback first: the safe choice is the default (Enter / Esc without moving).
            var choice = MuxConsole.Select("Where should the share listen?", [local, all, cancel]);
            if (choice == cancel) { MuxConsole.WriteMuted("Share cancelled."); return; }
            lan = choice == all;
        }

        try
        {
            await ShareHost.StartAsync(lan.Value);
        }
        catch (Exception ex)
        {
            MuxConsole.WriteError($"Could not start sharing: {ex.Message}");
            return;
        }
        MuxConsole.WriteSuccess("Sharing this session. Anyone with the link can ASK to watch - you approve each person.");
        PrintLinks();
        MuxConsole.WriteMuted("Guests are view-only. /share status  \u00b7  /share kick <name>  \u00b7  /share stop");
    }

    private static void PrintLinks()
    {
        var link = ShareHost.Link;
        if (link is null) return;
        string secretPart = link.ToString()[link.ToString().IndexOf('#')..];
        foreach (var addr in ShareHost.Addresses)
            MuxConsole.WriteInfo($"  mux-swarm --join \"http://{addr}/s/{link.RoomId}{secretPart}\"");
        if (ShareHost.IsLan)
            MuxConsole.WriteMuted("  (pick the address on the guest's network; Windows may ask to allow the firewall)");
        else
            MuxConsole.WriteMuted("  (loopback only: joinable from this machine. /share stop, then /share --lan for other machines)");
    }

    private static void PrintStatus()
    {
        if (!ShareHost.IsActive) { MuxConsole.WriteMuted("Not sharing. Start with /share"); return; }
        MuxConsole.WriteInfo($"Sharing on {(ShareHost.IsLan ? "all interfaces" : "loopback")}. Join with:");
        PrintLinks();
        var people = ShareHost.Participants;
        MuxConsole.WriteInfo(people.Count == 0 ? "No one is watching yet."
            : "Watching: " + string.Join(", ", people.Select(p => $"#{p.Id} {p.Name} ({p.Address})")));
    }

    private static async Task JoinAsync(string arg)
    {
        if (!ShareLink.TryParse(arg, out var link, out var err))
        {
            MuxConsole.WriteMuted($"/join: {err}. Usage: /join \"http://host:port/s/<room>#<secret>\"");
            return;
        }
        if (ShareHost.IsActive)
        {
            MuxConsole.WriteWarning("Stop your own share (/share stop) before joining another session.");
            return;
        }

        string reason;
        MuxConsole.TuiSuspend();
        using (EscapeKeyListener.SuspendInput())
        {
            bool prevCtrlC = false;
            try { prevCtrlC = Console.TreatControlCAsInput; Console.TreatControlCAsInput = true; } catch { }
            try
            {
                reason = await ShareGuest.RunAsync(link!, ShareGuest.ConsoleRead, s => { Console.Out.Write(s); Console.Out.Flush(); });
            }
            catch (ShareJoinException ex) { reason = "could not join: " + ex.Message; }
            catch (Exception ex) { reason = "join failed: " + ex.Message; }
            finally
            {
                try { Console.TreatControlCAsInput = prevCtrlC; } catch { }
            }
        }
        MuxConsole.TuiResume();
        MuxConsole.WriteMuted($"Left the shared session: {AnsiSanitizer.PlainText(reason)}");
    }
}
