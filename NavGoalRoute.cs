using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotNav;

// m_moveToState block  (the far / requested destination).
public partial class BotNavPlugin
{
    // RouteType enum
    // 0 Default, 1 Fastest, 2 Safest, 3 Retreat.
    private static string RouteName(int r) => r switch
    {
        0 => "Default", 1 => "Fastest", 2 => "Safest", 3 => "Retreat", _ => $"#{r}"
    };

    private void RegisterGoalRouteCommands()
    {
        // READ: print the requested destination + route for each selected bot.
        AddCommand("bot_nav_goal",
            "Read the requested MoveTo destination. Usage: bot_nav_goal <target>", CmdNavGoal);

        // WRITE: set the route style of an active MoveTo in place.
        AddCommand("bot_nav_setroute",
            "Set the route style (0-3) of an active MoveTo. Usage: bot_nav_setroute <target> <0-3>", CmdNavSetRoute);
    }

    // bot_nav_goal <target>
    // Output: requested goal = (x y z)  route = <name>  for each bot.
    // The destination point the MoveTo behaviour is heading for.
    private void CmdNavGoal(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireMoveToReady(caller)) return;
        if (info.ArgCount < 2) { Reply(caller, "Usage: bot_nav_goal <target>"); return; }

        var bots = ResolveBots(info.GetArg(1));
        if (bots.Count == 0) { Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'."); return; }

        foreach (var (bot, c, _) in bots)
        {
            // m_moveToState is gated on the MoveTo signature region being valid.
            Vector goal = NavMem.ReadVec(bot.Handle, NavMem.MoveStateGoal);
            int route   = NavMem.ReadInt(bot.Handle, NavMem.MoveStateRoute);
            Reply(caller,
                $"[BotNav] {c.PlayerName}: requested goal = ({goal.X:0.#}, {goal.Y:0.#}, {goal.Z:0.#})  " +
                $"route = {RouteName(route)} ({route})");
        }
    }

    // bot_nav_setroute <target> <0-3>
    // Writes m_moveToState.m_routeType in place. Format: int 0..3. 
    // Changes the route style the next path rebuild will use; does not itself rebuild.
    private void CmdNavSetRoute(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireMoveToReady(caller)) return;
        if (info.ArgCount < 3) { Reply(caller, "Usage: bot_nav_setroute <target> <0-3>"); return; }
        if (!int.TryParse(info.GetArg(2), out int route) || route < 0 || route > 3)
        { Reply(caller, "[BotNav] route must be 0(Default) 1(Fastest) 2(Safest) 3(Retreat)."); return; }

        var bots = ResolveBots(info.GetArg(1));
        foreach (var (bot, c, _) in bots)
        {
            NavMem.WriteInt(bot.Handle, NavMem.MoveStateRoute, route);
            Reply(caller, $"[BotNav] {c.PlayerName}: route set to {RouteName(route)} ({route}).");
        }
        if (bots.Count == 0) Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'.");
    }

    // The m_moveToState block is only trustworthy when the MoveTo signature resolved.
    private bool RequireMoveToReady(CCSPlayerController? caller)
    {
        if (_moveToReady) return true;
        Reply(caller, "[BotNav] MoveTo signature unresolved - m_moveToState read/write disabled.");
        return false;
    }
}