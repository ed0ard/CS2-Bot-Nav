using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotNav;

// The computed path and its target points / nav areas.
public partial class BotNavPlugin
{
    private readonly record struct PathNodeSnapshot(int Index, Vector Position, int AreaId, int Type, int How);

    private readonly record struct PathSnapshot(int Index, int Length, PathNodeSnapshot[] Nodes);

    private void RegisterPathAreaCommands()
    {
        AddCommand("bot_nav_path",
            "Read current goal, path index/length, endpoint point & nav-area id. Usage: bot_nav_path <target>", CmdNavPath);
        AddCommand("bot_nav_fullpath",
            "Read every valid node in the bot's current native path. Usage: bot_nav_fullpath <target>", CmdBotMovePath);
    }

    // Print a validated read-only snapshot of every native path node
    private void CmdBotMovePath(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireNavReady(caller)) return;
        if (info.ArgCount < 2) { Reply(caller, "Usage: bot_nav_fullpath <target>"); return; }

        var bots = ResolveBots(info.GetArg(1));
        if (bots.Count == 0) { Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'."); return; }

        foreach (var (bot, controller, _) in bots)
        {
            if (!TryReadPathSnapshot(bot.Handle, out var snapshot, out var error))
            {
                Reply(caller, $"[BotNav] {controller.PlayerName}: {error}");
                continue;
            }

            Reply(caller,
                $"[BotNav] {controller.PlayerName}: full path index={snapshot.Index} length={snapshot.Length} " +
                $"remaining={Math.Max(0, snapshot.Length - snapshot.Index)}");

            foreach (var node in snapshot.Nodes)
            {
                string state = PathNodeState(node.Index, snapshot.Index, snapshot.Length);
                Reply(caller,
                    $"[BotNav] [{node.Index:D3}] {state,-5} " +
                    $"pos=({node.Position.X:0.##},{node.Position.Y:0.##},{node.Position.Z:0.##}) " +
                    $"area={node.AreaId} type={PathSegmentTypeName(node.Type)} " +
                    $"how={PathNodeTraverseName(node.Index, snapshot.Length, node.How)}");
            }
        }
    }

    // Copy the active native path only when its bounds remain stable
    private static bool TryReadPathSnapshot(nint botHandle, out PathSnapshot snapshot, out string error)
    {
        snapshot = default;
        error = "path changed while reading";

        for (int attempt = 0; attempt < 2; attempt++)
        {
            int firstIndex = NavMem.ReadInt(botHandle, NavMem.PathIndex);
            int firstLength = NavMem.ReadInt(botHandle, NavMem.PathLength);
            if (!ValidatePathBounds(firstIndex, firstLength, out error))
                return false;

            if (firstLength == 0)
            {
                snapshot = new PathSnapshot(firstIndex, firstLength, Array.Empty<PathNodeSnapshot>());
                error = string.Empty;
                return true;
            }

            var nodes = new PathNodeSnapshot[firstLength];
            for (int index = 0; index < firstLength; index++)
            {
                nint element = NavMem.Elem(botHandle, index);
                Vector position = NavMem.ReadVec(element, NavMem.ElemPos);
                if (!IsFinite(position))
                {
                    error = $"node {index} contains a non-finite position";
                    return false;
                }

                nodes[index] = new PathNodeSnapshot(
                    index,
                    position,
                    NavMem.ReadInt(element, NavMem.ElemAreaId),
                    NavMem.ReadInt(element, NavMem.ElemType),
                    NavMem.ReadInt(element, NavMem.ElemHow));
            }

            int secondIndex = NavMem.ReadInt(botHandle, NavMem.PathIndex);
            int secondLength = NavMem.ReadInt(botHandle, NavMem.PathLength);
            if (firstIndex == secondIndex && firstLength == secondLength)
            {
                snapshot = new PathSnapshot(firstIndex, firstLength, nodes);
                error = string.Empty;
                return true;
            }
        }

        return false;
    }

    // Validate the active range before dereferencing path elements
    private static bool ValidatePathBounds(int index, int length, out string error)
    {
        if (length < 0 || length > NavMem.PathCount)
        {
            error = $"invalid path length {length}";
            return false;
        }

        if (length == 0)
        {
            error = string.Empty;
            return true;
        }

        if (index < 0 || index > length)
        {
            error = $"invalid path index {index} for length {length}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    // Check all vector components before formatting the snapshot
    private static bool IsFinite(Vector value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    // Label each node relative to the bot's current path index
    private static string PathNodeState(int nodeIndex, int pathIndex, int pathLength)
    {
        if (nodeIndex == pathLength - 1) return "END";
        if (nodeIndex < pathIndex) return "PAST";
        if (nodeIndex == pathIndex) return "NEXT";
        return "AHEAD";
    }

    // Hide undefined boundary traversal values on the first and final path nodes
    private static string PathNodeTraverseName(int nodeIndex, int pathLength, int how)
    {
        if (pathLength == 1) return "START_END";
        if (nodeIndex == 0) return "START";
        if (nodeIndex == pathLength - 1) return "END";
        return NavTraverseName(how);
    }

    // Return the traversal label stored on a native Path::Segment
    private static string PathSegmentTypeName(int type) => type switch
    {
        0 => "ON_GROUND",
        1 => "DROP_DOWN",
        2 => "CLIMB_UP",
        3 => "JUMP_OVER_GAP",
        4 => "LADDER_UP",
        5 => "LADDER_DOWN",
        _ => $"#{type}"
    };

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
        7 => "GO_ELEVATOR_UP", 8 => "GO_ELEVATOR_DOWN", 9 => "NONE", _ => $"#{how}"
    };

    private bool RequireNavReady(CCSPlayerController? caller)
    {
        if (NavMem.Ready) return true;
        Reply(caller, "[BotNav] nav offsets unresolved - read/write disabled.");
        return false;
    }
}
