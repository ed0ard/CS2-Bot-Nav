using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotNav;

// NavMem - in-process read/write of CCSBot navigation fields.
//
// The CS2 server runs in the same process as the plugin, so a CCSBot* handle
// (CCSBotPawn.Bot.Handle) is a real, directly-dereferenceable pointer. We read
// and write fields at (handle + offset) with unsafe pointers.
internal static class NavMem
{
    public static bool Ready { get; private set; }

    // --- schema-resolved field offsets -------------------------------------
    public static int GoalPosition;        // Vector  : current per-frame steering target
    public static int GoalEntity;          // CHandle : goal entity (bomb/hostage/...)
    public static int TaskEntity;          // CHandle : entity bound to current task
    public static int Task;                // int     : TaskType enum (= TaskEntity - 4)
    public static int PathIndex;           // int     : index of next path node
    public static int PathLength;          // int     : number of nodes in m_path (= PathIndex - 0x88)
    public static int PathBase;            // ConnectInfo m_path[256] base (= PathIndex - 0x4888)
    public static int AreaEnteredTimestamp;// float

    // --- constant intra-struct layout -------------------------------------
    public const int ConnectInfoStride = 0x48;  // sizeof(ConnectInfo)
    public const int PathCount         = 256;   // MAX_PATH_LENGTH
    public const int ElemHow           = 0x04;  // ConnectInfo.how    (int NavTraverseType: how this node is entered)
    public const int ElemPos           = 0x2C;  // ConnectInfo.pos    (Vector: world goal point of this node)
    public const int ElemAreaId        = 0x3C;  // ConnectInfo.areaId (int: nav-area id resolved through the nav mesh)

    // --- m_moveToState block ----------------------------------------------
    public const int MoveStateGoal  = 0x2E8;    // Vector : the far/requested destination
    public const int MoveStateRoute = 0x2F4;    // int    : RouteType passed to MoveTo()

    // CEntityHandle invalid sentinel.
    public const uint InvalidHandle = 0xFFFFFFFF;

    public static void Resolve(ILogger log)
    {
        try
        {
            GoalPosition        = Schema.GetSchemaOffset("CCSBot", "m_goalPosition");
            GoalEntity          = Schema.GetSchemaOffset("CCSBot", "m_goalEntity");
            TaskEntity          = Schema.GetSchemaOffset("CCSBot", "m_taskEntity");
            PathIndex           = Schema.GetSchemaOffset("CCSBot", "m_pathIndex");
            AreaEnteredTimestamp= Schema.GetSchemaOffset("CCSBot", "m_areaEnteredTimestamp");

            Task       = TaskEntity - 4;          // TaskType sits immediately before m_taskEntity
            PathLength = PathIndex  - 0x88;        // constant delta, verified across builds
            PathBase   = PathIndex  - 0x4888;      // m_path[] base, constant delta

            Ready = GoalPosition > 0 && PathIndex > 0;
            if (Ready)
                log.LogInformation(
                    $"[BotNav] NavMem resolved: goalPos=0x{GoalPosition:X} goalEnt=0x{GoalEntity:X} " +
                    $"task=0x{Task:X} pathIndex=0x{PathIndex:X} pathLen=0x{PathLength:X} pathBase=0x{PathBase:X}");
            else
                log.LogError("[BotNav] NavMem: schema returned invalid offsets; nav read/write disabled.");
        }
        catch (Exception ex)
        {
            Ready = false;
            log.LogError($"[BotNav] NavMem.Resolve failed: {ex.Message}");
        }
    }

    // --- raw primitives (handle is a live in-process CCSBot*) --------------
    public static unsafe int   ReadInt  (nint h, int off) => *(int*)  (h + off);
    public static unsafe float ReadFloat(nint h, int off) => *(float*)(h + off);
    public static unsafe uint  ReadUInt (nint h, int off) => *(uint*) (h + off);

    public static unsafe void  WriteInt  (nint h, int off, int v)   => *(int*)  (h + off) = v;
    public static unsafe void  WriteFloat(nint h, int off, float v) => *(float*)(h + off) = v;

    public static unsafe Vector ReadVec(nint h, int off)
    {
        float* p = (float*)(h + off);
        return new Vector(p[0], p[1], p[2]);
    }

    // Address of ConnectInfo element i within m_path[].
    public static nint Elem(nint botHandle, int i) => botHandle + PathBase + i * ConnectInfoStride;

    // Entry index encoded in a CEntityHandle (lower 15 bits), or -1 if invalid.
    public static int HandleToIndex(uint raw) => raw == InvalidHandle ? -1 : (int)(raw & 0x7FFF);
}

public partial class BotNavPlugin
{
    // Resolve a slot to its live CCSBot*.
    // Returns false unless the bot is valid and active this frame.
    internal static bool TryGetBot(int slot, out CCSBot bot, out CCSPlayerController controller, out CCSPlayerPawn pawn)
    {
        bot = null!; controller = null!; pawn = null!;

        var c = Utilities.GetPlayerFromSlot(slot);
        if (c == null || !c.IsValid || !c.PawnIsAlive)
            return false;

        var p = c.PlayerPawn.Value;
        if (p == null || !p.IsValid || !p.BotAllowActive)
            return false;

        var b = p.Bot;
        if (b == null || b.Handle == nint.Zero)
            return false;

        controller = c; pawn = p; bot = b;
        return true;
    }

    // Resolve a target token (all|t|ct|name|slot|weapon) to the live, active bots it selects.
    private List<(CCSBot bot, CCSPlayerController controller, CCSPlayerPawn pawn)> ResolveBots(string token)
    {
        var res = new List<(CCSBot, CCSPlayerController, CCSPlayerPawn)>();
        foreach (var slot in ResolveTargets(token))
            if (TryGetBot(slot, out var b, out var c, out var p))
                res.Add((b, c, p));
        return res;
    }

    // Echo to the calling player's console, or the server console for rcon/server calls.
    private static void Reply(CCSPlayerController? caller, string msg)
    {
        if (caller != null && caller.IsValid)
            caller.PrintToConsole(msg);
        else
            Server.PrintToConsole(msg);
    }
}
