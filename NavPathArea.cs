using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotNav;

// The computed path and its target points / nav areas.
public partial class BotNavPlugin
{
    private void RegisterPathAreaCommands()
    {
        AddCommand("bot_nav_path",
            "Read current goal, path index/length, endpoint point & nav-area id. Usage: bot_nav_path <target>", CmdNavPath);
    }

    // bot_nav_path <target>
    // Output per bot:
    //   pathIndex= Index/Length  current=(x y z)  next=(x y z) area=<id>
    //   endpoint=(x y z) area=<id> how=<NavTraverseType>
    // current = where it is stepping now; next = the node it walks to
    // next (its area id); endpoint = the final destination point and its area id.
    private void CmdNavPath(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireNavReady(caller)) return;
        if (info.ArgCount < 2) { Reply(caller, "Usage: bot_nav_path <target>"); return; }

        var bots = ResolveBots(info.GetArg(1));
        if (bots.Count == 0) { Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'."); return; }

        foreach (var (bot, c, _) in bots)
        {
            nint h = bot.Handle;
            Vector goal = NavMem.ReadVec(h, NavMem.GoalPosition);   // current steering target
            int idx     = NavMem.ReadInt(h, NavMem.PathIndex);
            int len     = NavMem.ReadInt(h, NavMem.PathLength);

            if (len <= 0 || len > NavMem.PathCount)
            {
                Reply(caller, $"[BotNav] {c.PlayerName}: no path. current goal = " +
                              $"({goal.X:0.#}, {goal.Y:0.#}, {goal.Z:0.#})");
                continue;
            }

            int    nextI    = idx < len ? idx : len - 1;
            Vector nextPos  = NavMem.ReadVec(NavMem.Elem(h, nextI),   NavMem.ElemPos);
            int    nextArea = NavMem.ReadInt(NavMem.Elem(h, nextI),   NavMem.ElemAreaId);
            int    nextHow  = NavMem.ReadInt(NavMem.Elem(h, nextI),   NavMem.ElemHow);
            Vector endPos   = NavMem.ReadVec(NavMem.Elem(h, len - 1), NavMem.ElemPos);
            int    endArea  = NavMem.ReadInt(NavMem.Elem(h, len - 1), NavMem.ElemAreaId);

            Reply(caller,
                $"[BotNav] {c.PlayerName}: pathIndex={idx}/{len}  " +
                $"current=({goal.X:0.#},{goal.Y:0.#},{goal.Z:0.#})  " +
                $"next=({nextPos.X:0.#},{nextPos.Y:0.#},{nextPos.Z:0.#}) area={nextArea} how={NavTraverseName(nextHow)}");
            Reply(caller,
                $"           endpoint=({endPos.X:0.#},{endPos.Y:0.#},{endPos.Z:0.#}) area={endArea}");
        }
    }

    // NavTraverseType label (how a node's area is entered from the previous one).
    private static string NavTraverseName(int how) => how switch
    {
        0 => "GO_NORTH", 1 => "GO_EAST", 2 => "GO_SOUTH", 3 => "GO_WEST",
        4 => "GO_LADDER_UP", 5 => "GO_LADDER_DOWN", 6 => "GO_JUMP",
        7 => "GO_ELEVATOR_UP", 8 => "GO_ELEVATOR_DOWN", _ => $"#{how}"
    };

    private bool RequireNavReady(CCSPlayerController? caller)
    {
        if (NavMem.Ready) return true;
        Reply(caller, "[BotNav] nav offsets unresolved - read/write disabled.");
        return false;
    }
}
