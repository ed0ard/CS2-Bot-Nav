using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;

namespace BotNav;

// The high-level task and its bound entities.
// This is why the bot picked its current destination.
//   m_taskEntity (CHandle) - entity attached to the task (hostage, bomb).
//   m_goalEntity (CHandle) - the navigation goal entity (bomb to fetch, hostage).
public partial class BotNavPlugin
{
    // TaskType labels
    private static readonly string[] TaskNames =
    {
        "SEEK_AND_DESTROY",                   //  0
        "PLANT_BOMB",                         //  1
        "FIND_TICKING_BOMB",                  //  2
        "DEFUSE_BOMB",                        //  3
        "GUARD_TICKING_BOMB",                 //  4
        "GUARD_BOMB_DEFUSER",                 //  5
        "GUARD_LOOSE_BOMB",                   //  6
        "GUARD_BOMB_ZONE",                    //  7
        "GUARD_INITIAL_ENCOUNTER",            //  8
        "ESCAPE_FROM_BOMB",                   //  9
        "HOLD_POSITION",                      // 10
        "FOLLOW",                             // 11
        "VIP_ESCAPE",                         // 12
        "GUARD_VIP_ESCAPE_ZONE",              // 13
        "COLLECT_HOSTAGES",                   // 14
        "RESCUE_HOSTAGES",                    // 15
        "GUARD_HOSTAGES",                     // 16
        "GUARD_HOSTAGE_RESCUE_ZONE",          // 17
        "MOVE_TO_LAST_KNOWN_ENEMY_POSITION",  // 18
        "MOVE_TO_SNIPER_SPOT",                // 19
        "SNIPING",                            // 20
        "ESCAPE_FROM_FLAMES",                 // 21
    };

    private static string TaskName(int t) =>
        (t >= 0 && t < TaskNames.Length) ? TaskNames[t] : $"#{t}";

    private void RegisterTaskEntityCommands()
    {
        AddCommand("bot_nav_task",
            "Read task + task/goal entities. Usage: bot_nav_task <target>", CmdNavTask);
        AddCommand("bot_nav_settask",
            "Write m_task (objective). Usage: bot_nav_settask <target> <taskId 0-21>", CmdNavSetTask);
    }

    // bot_nav_task <target>
    private void CmdNavTask(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireNavReady(caller)) return;
        if (info.ArgCount < 2) { Reply(caller, "Usage: bot_nav_task <target>"); return; }

        var bots = ResolveBots(info.GetArg(1));
        if (bots.Count == 0) { Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'."); return; }

        foreach (var (bot, c, _) in bots)
        {
            nint h = bot.Handle;
            int task     = NavMem.ReadInt(h, NavMem.Task);
            uint taskEnt = NavMem.ReadUInt(h, NavMem.TaskEntity);
            uint goalEnt = NavMem.ReadUInt(h, NavMem.GoalEntity);
            Reply(caller,
                $"[BotNav] {c.PlayerName}: task = {TaskName(task)} ({task})  " +
                $"taskEntity = {DescribeHandle(taskEnt)}  goalEntity = {DescribeHandle(goalEnt)}");
        }
    }

    // bot_nav_settask <target> <taskId>
    // Writes m_task (int). Format: 0..21 per TaskNames above.
    private void CmdNavSetTask(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireNavReady(caller)) return;
        if (info.ArgCount < 3) { Reply(caller, "Usage: bot_nav_settask <target> <taskId 0-21>"); return; }
        if (!int.TryParse(info.GetArg(2), out int task) || task < 0 || task > 21)
        { Reply(caller, "[BotNav] taskId must be 0-21 (see bot_nav_task output for names)."); return; }

        var bots = ResolveBots(info.GetArg(1));
        foreach (var (bot, c, _) in bots)
        {
            NavMem.WriteInt(bot.Handle, NavMem.Task, task);
            Reply(caller, $"[BotNav] {c.PlayerName}: task set to {TaskName(task)} ({task}).");
        }
        if (bots.Count == 0) Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'.");
    }

    // Render a CEntityHandle as "<index>:<name>" or "none".
    private static string DescribeHandle(uint raw)
    {
        int idx = NavMem.HandleToIndex(raw);
        if (idx < 0) return "none";
        try
        {
            var ent = Utilities.GetEntityFromIndex<CBaseEntity>(idx);
            if (ent != null && ent.IsValid)
                return $"{idx}:{ent.DesignerName}";
        }
        catch { /* index not resolvable */ }
        return idx.ToString();
    }
}
