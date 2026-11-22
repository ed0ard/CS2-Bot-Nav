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

    // Native fields relative to schema anchors.
    public static int Task;
    public static int PathLength;
    public static int PathBase;

    // --- constant intra-struct layout -------------------------------------
    public const int PathSegmentStride = 0x48;  // sizeof(Path::Segment)
    public const int PathCount         = 256;   // MAX_PATH_LENGTH
    public const int ElemHow           = 0x00;  // Path::Segment.how    (int NavTraverseType: how this node is entered)
    public const int ElemType          = 0x04;  // Path::Segment.type   (int SegmentType: how this segment is traversed)
    public const int ElemPos           = 0x2C;  // Path::Segment.pos    (Vector: world goal point of this node)
    public const int ElemAreaId        = 0x3C;  // Path::Segment.areaId (int: nav-area id resolved through the nav mesh)

    // --- m_moveToState block ----------------------------------------------
    public static readonly int MoveStateGoal  = OperatingSystem.IsLinux() ? 0x2E0 : 0x2E8;
    public static readonly int MoveStateRoute = OperatingSystem.IsLinux() ? 0x2EC : 0x2F4;

    public static void Resolve(ILogger log)
    {
        try
        {
            int taskEntity = Schema.GetSchemaOffset("CCSBot", "m_taskEntity");
            int pathIndex = Schema.GetSchemaOffset("CCSBot", "m_pathIndex");
            Task       = taskEntity - 4;
            PathLength = pathIndex - 0x78;
            PathBase   = pathIndex - 0x4878;

            Ready = Task > 0 && PathBase > 0;
            if (Ready)
                log.LogInformation($"[BotNav] NavMem resolved: task=0x{Task:X} pathIndex=0x{pathIndex:X} pathLen=0x{PathLength:X} pathBase=0x{PathBase:X}");
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

    public static unsafe void  WriteInt  (nint h, int off, int v)   => *(int*)  (h + off) = v;
    public static unsafe void  WriteFloat(nint h, int off, float v) => *(float*)(h + off) = v;

    public static unsafe Vector ReadVec(nint h, int off)
    {
        float* p = (float*)(h + off);
        return new Vector(p[0], p[1], p[2]);
    }

    // Return the address of Path::Segment element i within m_path[]
    public static nint Elem(nint botHandle, int i) => botHandle + PathBase + i * PathSegmentStride;

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
