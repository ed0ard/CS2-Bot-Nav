using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotNav;

// Drives CS2 bot navigation by repeatedly calling the native CCSBot::MoveTo.
//   BotNav.cs          - core: MoveTo native, target resolution, move/hold/release/follow
//   NavMemory.cs       - schema offset resolver, raw in-process read/write helpers
//   NavGoalRoute.cs    - m_moveToState goal/route  (read/write)
//   NavPathArea.cs     - m_goalPosition, path index/length, endpoint/area id
//   NavTaskEntity.cs   - m_task, m_taskEntity, m_goalEntity  (read/write)
//   NavBombsite.cs     - which bombsite (A/B) the goal / bot is at
public partial class BotNavPlugin : BasePlugin
{
    public override string ModuleName => "BotNav";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription => "Per-bot navigation read & control.";

    // void CCSBot::MoveTo(CCSBot* this, const Vector* pos, RouteType route)  __fastcall
    // Signature verified on server.dll 6.2
    private const string MoveToSignature =
        "F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?";

    // RouteType enum
    public enum RouteType
    {
        Default = 0,
        Fastest = 1,
        Safest  = 2,
        Retreat = 3,
    }

    // Per-bot control entry. Hold mode re-points the goal at the bot's own origin;
    // Follow mode re-points at a leader bot's live position every cycle.
    private sealed class ControlEntry
    {
        public bool Hold;
        public float X, Y, Z;          // destination (ignored when Hold==true or FollowSlot!=null)
        public RouteType Route = RouteType.Fastest;
        public int? FollowSlot;        // when set, this bot follows the leader at this slot
    }

    // CCSBot* (arg1), Vector* (arg2), int route (arg3). IntPtr maps to DATA_TYPE_POINTER.
    private MemoryFunctionVoid<nint, nint, int>? _moveTo;
    private bool _moveToReady;

    // Reach threshold (units) below which a moveto goal is considered done.
    private const float ReachThresholdSqr = 40.0f * 40.0f;

    // slot -> control entry
    private readonly Dictionary<int, ControlEntry> _controlled = new();

    // Reusable Vector whose native buffer backs the pointer we pass to MoveTo.
    private readonly Vector _argVec = new(0, 0, 0);

    // Singleton instance for cross-plugin API access.
    public static BotNavPlugin? Instance { get; private set; }

    public override void Load(bool hotReload)
    {
        try
        {
            _moveTo = new MemoryFunctionVoid<nint, nint, int>(MoveToSignature);
            _moveToReady = true;
        }
        catch (Exception ex)
        {
            _moveToReady = false;
            Logger.LogError($"[BotNav] Failed to resolve CCSBot::MoveTo signature, MoveTo disabled: {ex.Message}");
        }

        // Resolve all CCSBot navigation field offsets from the live schema.
        NavMem.Resolve(Logger);

        Instance = this;

        AddTimer(0.1f, ReissueGoals, TimerFlags.REPEAT);

        // Suppress BotState un-stuck logic for held bots: write IsStuck=false every tick.
        RegisterListener<Listeners.OnTick>(SuppressStuckForHeldBots);

        // WRITE: set the navigation goal via the native CCSBot::MoveTo (persistent redirect).
        AddCommand("bot_nav_setgoal", "Set bot(s) navigation goal to a world position. Usage: bot_nav_setgoal <target> <x> <y> <z> [route]", CmdMoveTo);
        AddCommand("bot_nav_hold", "Hold bot(s) at their current position. Usage: bot_nav_hold <target>", CmdHold);
        AddCommand("bot_nav_release", "Release bot(s) back to behavior-tree control. Usage: bot_nav_release <target>", CmdRelease);
        AddCommand("bot_nav_follow", "Make bot(s) follow another bot. Usage: bot_nav_follow <followers> <leader>", CmdFollow);

        // Feature commands
        RegisterGoalRouteCommands();   // NavGoalRoute.cs
        RegisterPathAreaCommands();    // NavPathArea.cs
        RegisterTaskEntityCommands();  // NavTaskEntity.cs
        RegisterBombsiteCommands();    // NavBombsite.cs
    }

    public override void Unload(bool hotReload)
    {
        _controlled.Clear();
        Instance = null;
    }

    // ---- Core: resolve CCSBot* and invoke MoveTo ----------------------------------

    private void InvokeMoveTo(CCSBot bot, float x, float y, float z, RouteType route)
    {
        if (!_moveToReady || _moveTo == null || bot.Handle == nint.Zero)
            return;

        // Write into the reusable Vector's native buffer, then pass its Handle as Vector*.
        _argVec.X = x;
        _argVec.Y = y;
        _argVec.Z = z;

        _moveTo.Invoke(bot.Handle, _argVec.Handle, (int)route);
    }

    // 0.1s loop: re-issue every controlled bot's goal to override the behavior tree.
    private void ReissueGoals()
    {
        if (_controlled.Count == 0)
            return;

        // Snapshot keys so we can prune finished entries while iterating.
        foreach (var slot in _controlled.Keys.ToList())
        {
            var controller = Utilities.GetPlayerFromSlot(slot);
            if (controller == null || !controller.IsValid || !controller.PawnIsAlive)
            {
                _controlled.Remove(slot);
                continue;
            }

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
            {
                _controlled.Remove(slot);
                continue;
            }

            // Honor the same guard the game uses before touching the bot, and fetch CCSBot*.
            if (!pawn.BotAllowActive)
                continue; // bot not active this cycle; keep entry and retry next loop

            var bot = pawn.Bot;
            if (bot == null || bot.Handle == nint.Zero)
                continue;

            var entry = _controlled[slot];
            var origin = pawn.AbsOrigin;
            if (origin == null)
                continue;

            if (entry.Hold)
            {
                // Re-point goal at current origin: stop moving, keep attack and aim alive.
                InvokeMoveTo(bot, origin.X, origin.Y, origin.Z, entry.Route);
                continue;
            }

            // Follow mode: re-point goal at leader position every cycle. Leader dead → stop.
            if (entry.FollowSlot.HasValue)
            {
                var leaderCtrl = Utilities.GetPlayerFromSlot(entry.FollowSlot.Value);
                if (leaderCtrl == null || !leaderCtrl.IsValid || !leaderCtrl.PawnIsAlive)
                {
                    _controlled.Remove(slot);
                    continue;
                }
                var leaderPawn = leaderCtrl.PlayerPawn.Value;
                if (leaderPawn == null || !leaderPawn.IsValid || leaderPawn.AbsOrigin == null)
                {
                    _controlled.Remove(slot);
                    continue;
                }
                InvokeMoveTo(bot,
                    leaderPawn.AbsOrigin.X, leaderPawn.AbsOrigin.Y, leaderPawn.AbsOrigin.Z,
                    entry.Route);
                continue;
            }

            // Distant goal: stop once close enough, otherwise keep pushing the goal.
            float dx = origin.X - entry.X, dy = origin.Y - entry.Y, dz = origin.Z - entry.Z;
            if (dx * dx + dy * dy + dz * dz <= ReachThresholdSqr)
            {
                _controlled.Remove(slot);
                continue;
            }

            InvokeMoveTo(bot, entry.X, entry.Y, entry.Z, entry.Route);
        }
    }

    // Every tick: for held bots, write IsStuck=false to neutralise BotState's
    // un-stuck jump/velocity mechanism so the bot stays put.
    private void SuppressStuckForHeldBots()
    {
        if (_controlled.Count == 0)
            return;

        foreach (var kv in _controlled)
        {
            if (!kv.Value.Hold)
                continue;

            int slot = kv.Key;

            var controller = Utilities.GetPlayerFromSlot(slot);
            if (controller == null || !controller.IsValid || !controller.PawnIsAlive)
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null || bot.Handle == nint.Zero)
                continue;

            ref bool isStuck = ref bot.IsStuck;
            isStuck = false;
        }
    }

    // ---- Commands -----------------------------------------------------------------

    private void CmdMoveTo(CCSPlayerController? caller, CommandInfo info)
    {
        if (!_moveToReady)
        {
            Server.PrintToConsole("[BotNav] MoveTo unavailable: signature failed to resolve.");
            return;
        }

        // bot_nav_setgoal <target> <x> <y> <z> [route]
        if (info.ArgCount < 5)
        {
            Server.PrintToConsole("Usage: bot_nav_setgoal <all|t|ct|<name/slot>|sniper|rifle|smg|machinegun|pistol|shotgun> <x> <y> <z> [route]");
            return;
        }

        string targetArg = info.GetArg(1);

        if (!float.TryParse(info.GetArg(2), out float x) ||
            !float.TryParse(info.GetArg(3), out float y) ||
            !float.TryParse(info.GetArg(4), out float z))
        {
            Server.PrintToConsole("[BotNav] Invalid coordinates.");
            return;
        }

        var route = RouteType.Fastest;
        if (info.ArgCount >= 6 && !TryParseRoute(info.GetArg(5), out route))
        {
            Server.PrintToConsole("[BotNav] Invalid route. Use default|fastest|safest|retreat or 0-3.");
            return;
        }

        var bots = ResolveTargets(targetArg);
        if (bots.Count == 0)
        {
            Server.PrintToConsole($"[BotNav] No matching bots for '{targetArg}'.");
            return;
        }

        foreach (var slot in bots)
            _controlled[slot] = new ControlEntry { Hold = false, X = x, Y = y, Z = z, Route = route };

        Server.PrintToConsole($"[BotNav] Moving {bots.Count} bot(s) to ({x:0.#}, {y:0.#}, {z:0.#}) via {route}.");
    }

    private void CmdHold(CCSPlayerController? caller, CommandInfo info)
    {
        if (!_moveToReady)
        {
            Server.PrintToConsole("[BotNav] Hold unavailable: signature failed to resolve.");
            return;
        }

        if (info.ArgCount < 2)
        {
            Server.PrintToConsole("Usage: bot_nav_hold <all|t|ct|<name/slot>|sniper|rifle|smg|machinegun|pistol|shotgun>");
            return;
        }

        var bots = ResolveTargets(info.GetArg(1));
        if (bots.Count == 0)
        {
            Server.PrintToConsole($"[BotNav] No matching bots for '{info.GetArg(1)}'.");
            return;
        }

        foreach (var slot in bots)
            _controlled[slot] = new ControlEntry { Hold = true, Route = RouteType.Fastest };

        Server.PrintToConsole($"[BotNav] Holding {bots.Count} bot(s) in place.");
    }

    private void CmdRelease(CCSPlayerController? caller, CommandInfo info)
    {
        if (info.ArgCount < 2)
        {
            Server.PrintToConsole("Usage: bot_nav_release <all|t|ct|<name/slot>|sniper|rifle|smg|machinegun|pistol|shotgun>");
            return;
        }

        var bots = ResolveTargets(info.GetArg(1));
        int released = 0;
        foreach (var slot in bots)
            if (_controlled.Remove(slot))
                released++;

        Server.PrintToConsole($"[BotNav] Released {released} bot(s) to behavior-tree control.");
    }

    // bot_nav_follow <followers> <leader> — followers re-point at leader every 0.1s.
    // <leader> can be any alive player (bot or human), resolved by name or slot.
    // Follow stops when the leader dies or all followers are released/dead.
    private void CmdFollow(CCSPlayerController? caller, CommandInfo info)
    {
        if (!_moveToReady)
        {
            Server.PrintToConsole("[BotNav] Follow unavailable: signature failed to resolve.");
            return;
        }

        if (info.ArgCount < 3)
        {
            Server.PrintToConsole("Usage: bot_nav_follow <followers> <leader>");
            Server.PrintToConsole("  <followers>: all|t|ct|<name/slot>|sniper|rifle|smg|machinegun|pistol|shotgun");
            Server.PrintToConsole("  <leader>  : any alive player by name or slot (bot or human).");
            return;
        }

        // Resolve followers (first target) — can be a group via target syntax.
        var followers = ResolveTargets(info.GetArg(1));
        if (followers.Count == 0)
        {
            Server.PrintToConsole($"[BotNav] No matching follower bots for '{info.GetArg(1)}'.");
            return;
        }

        // Resolve leader (second target) — any player (bot or human), by name or slot.
        var leaders = ResolvePlayer(info.GetArg(2));
        if (leaders.Count == 0)
        {
            Server.PrintToConsole($"[BotNav] No matching player for leader '{info.GetArg(2)}'.");
            return;
        }
        if (leaders.Count > 1)
        {
            Server.PrintToConsole($"[BotNav] '{info.GetArg(2)}' matched {leaders.Count} players — leader must be exactly one (use name/slot).");
            return;
        }

        int leaderSlot = leaders[0];
        string leaderName = Utilities.GetPlayerFromSlot(leaderSlot)?.PlayerName ?? $"slot {leaderSlot}";

        // Prevent self-follow (only when leader is also a bot in the follower set).
        followers.Remove(leaderSlot);

        if (followers.Count == 0)
        {
            Server.PrintToConsole("[BotNav] No valid followers (leader cannot follow itself, or all followers are the leader).");
            return;
        }

        foreach (var slot in followers)
            _controlled[slot] = new ControlEntry { FollowSlot = leaderSlot, Route = RouteType.Fastest };

        Server.PrintToConsole($"[BotNav] {followers.Count} bot(s) now following '{leaderName}'.");
    }

    // ---- Public API for cross-plugin calls -----------------------------------------
    // Other plugins can call these after adding a <Reference> to BotNav.dll:
    //
    //   <Reference Include="BotNav">
    //     <HintPath>..\..\BotNav\bin\Debug\net8.0\BotNav.dll</HintPath>
    //     <Private>false</Private>
    //   </Reference>
    //
    // Usage:
    //   var nav = BotNavPlugin.Instance;
    //   if (nav == null) return;
    //   nav.Hold(slot: 3);
    //   var goal = nav.GetGoal(slot: 3);

    // ---- DTOs for read commands ----

    public struct NavGoalData
    {
        public float X, Y, Z;
        public RouteType Route;
    }

    public struct NavPathData
    {
        public int Index;
        public int Length;
        public float CurrentX, CurrentY, CurrentZ;
        public bool HasPath;
        public float NextX, NextY, NextZ;
        public int? NextAreaId;
        public string? NextHow;
        public float EndX, EndY, EndZ;
        public int? EndpointAreaId;
    }

    public struct NavTaskData
    {
        public int TaskType;
        public string? TaskName;
        public int? TaskEntityIndex;
        public string? TaskEntityName;
        public int? GoalEntityIndex;
        public string? GoalEntityName;
    }

    public struct NavSiteData
    {
        public int? GoalSiteIndex;
        public char GoalSiteLetter;
        public int? StandingSiteIndex;
        public char StandingSiteLetter;
        public bool HasSites;
    }

    /// Whether a bot is currently under BotNav movement control.
    /// "Controlled" means the bot's navigation goal is being overridden every 0.1s
    /// by BotNav (via SetGoal, Hold, Follow, or MoveToSite). A bot is no longer controlled after
    /// Release, reaching its goal, leader death (Follow), or bot death.
    public bool IsControlled(int slot)
    {
        return _controlled.ContainsKey(slot);
    }

    // ------------ write ------------

    public void SetGoal(int slot, float x, float y, float z, RouteType route = RouteType.Fastest)
    {
        _controlled[slot] = new ControlEntry { Hold = false, X = x, Y = y, Z = z, Route = route };
    }

    public void Hold(int slot)
    {
        _controlled[slot] = new ControlEntry { Hold = true, Route = RouteType.Fastest };
    }

    public void Follow(int slot, int leaderSlot)
    {
        _controlled[slot] = new ControlEntry { FollowSlot = leaderSlot, Route = RouteType.Fastest };
    }

    public bool Release(int slot)
    {
        return _controlled.Remove(slot);
    }

    public bool SetRoute(int slot, RouteType route)
    {
        if (!_moveToReady)
            return false;
        if (!TryGetBot(slot, out var bot, out _, out _))
            return false;

        NavMem.WriteInt(bot.Handle, NavMem.MoveStateRoute, (int)route);
        return true;
    }

    public bool SetTask(int slot, int taskType)
    {
        if (taskType < 0 || taskType > 21)
            return false;
        if (!NavMem.Ready)
            return false;
        if (!TryGetBot(slot, out var bot, out _, out _))
            return false;

        NavMem.WriteInt(bot.Handle, NavMem.Task, taskType);
        return true;
    }

    public bool MoveToSite(int slot, int siteIndex)
    {
        if (!_moveToReady)
            return false;
        if (!TryGetBot(slot, out _, out var controller, out _))
            return false;

        var sites = GetBombsites();
        if (sites.Count == 0 || siteIndex < 0 || siteIndex >= sites.Count)
            return false;

        Vector p = RandomPointInSite(sites[siteIndex]);
        _controlled[controller.Slot] = new ControlEntry
        {
            Hold = false, X = p.X, Y = p.Y, Z = p.Z, Route = RouteType.Fastest,
        };
        return true;
    }

    // ------------ read ------------

    public NavGoalData? GetGoal(int slot)
    {
        if (!_moveToReady)
            return null;
        if (!TryGetBot(slot, out var bot, out _, out _))
            return null;

        var goal = NavMem.ReadVec(bot.Handle, NavMem.MoveStateGoal);
        int route = NavMem.ReadInt(bot.Handle, NavMem.MoveStateRoute);

        return new NavGoalData
        {
            X = goal.X, Y = goal.Y, Z = goal.Z,
            Route = (RouteType)(route >= 0 && route <= 3 ? route : 1),
        };
    }

    public NavPathData? GetPath(int slot)
    {
        if (!NavMem.Ready)
            return null;
        if (!TryGetBot(slot, out var bot, out _, out _))
            return null;

        nint h = bot.Handle;
        var cur = NavMem.ReadVec(h, NavMem.GoalPosition);
        int idx = NavMem.ReadInt(h, NavMem.PathIndex);
        int len = NavMem.ReadInt(h, NavMem.PathLength);

        var data = new NavPathData
        {
            Index = idx,
            Length = len,
            CurrentX = cur.X, CurrentY = cur.Y, CurrentZ = cur.Z,
            HasPath = len > 0 && len <= NavMem.PathCount,
        };

        if (data.HasPath)
        {
            int nextI   = idx < len ? idx : len - 1;
            var nextPos  = NavMem.ReadVec(NavMem.Elem(h, nextI), NavMem.ElemPos);
            int nextArea = NavMem.ReadInt(NavMem.Elem(h, nextI), NavMem.ElemAreaId);
            int nextHow  = NavMem.ReadInt(NavMem.Elem(h, nextI), NavMem.ElemHow);
            var endPos   = NavMem.ReadVec(NavMem.Elem(h, len - 1), NavMem.ElemPos);
            int endArea  = NavMem.ReadInt(NavMem.Elem(h, len - 1), NavMem.ElemAreaId);

            data.NextX = nextPos.X; data.NextY = nextPos.Y; data.NextZ = nextPos.Z;
            data.NextAreaId = nextArea;
            data.NextHow = NavTraverseName(nextHow);
            data.EndX = endPos.X; data.EndY = endPos.Y; data.EndZ = endPos.Z;
            data.EndpointAreaId = endArea;
        }

        return data;
    }

    public NavTaskData? GetTask(int slot)
    {
        if (!NavMem.Ready)
            return null;
        if (!TryGetBot(slot, out var bot, out _, out _))
            return null;

        nint h = bot.Handle;
        int task     = NavMem.ReadInt(h, NavMem.Task);
        uint taskEnt = NavMem.ReadUInt(h, NavMem.TaskEntity);
        uint goalEnt = NavMem.ReadUInt(h, NavMem.GoalEntity);

        return new NavTaskData
        {
            TaskType = task,
            TaskName = TaskName(task),
            TaskEntityIndex = NavMem.HandleToIndex(taskEnt),
            TaskEntityName = DescribeHandle(taskEnt),
            GoalEntityIndex = NavMem.HandleToIndex(goalEnt),
            GoalEntityName = DescribeHandle(goalEnt),
        };
    }

    public NavSiteData? GetSite(int slot)
    {
        if (!_moveToReady)
            return null;
        if (!TryGetBot(slot, out var bot, out _, out var pawn))
            return null;

        var sites = GetBombsites();
        if (sites.Count == 0)
        {
            return new NavSiteData
            {
                GoalSiteIndex = null, GoalSiteLetter = '?',
                StandingSiteIndex = null, StandingSiteLetter = '?',
                HasSites = false,
            };
        }

        Vector goal = NavMem.ReadVec(bot.Handle, NavMem.MoveStateGoal);
        int goalSite = NearestSite(sites, goal);
        int hereSite = pawn.AbsOrigin != null ? SiteContaining(sites, pawn.AbsOrigin) : -1;

        return new NavSiteData
        {
            GoalSiteIndex = goalSite >= 0 ? goalSite : null,
            GoalSiteLetter = goalSite >= 0 ? sites[goalSite].Letter : '?',
            StandingSiteIndex = hereSite >= 0 ? hereSite : null,
            StandingSiteLetter = hereSite >= 0 ? sites[hereSite].Letter : '?',
            HasSites = true,
        };
    }

    // ---- Target resolution --------------------------------------------------------

    // Returns the slots of alive bots matching the target token:
    //   all | t | ct | <bot name or slot> | weapon-type keyword
    private List<int> ResolveTargets(string token)
    {
        var result = new List<int>();
        token = token.Trim();

        bool isWeaponFilter = TryParseWeaponType(token, out var wantedType);
        bool isAll = token.Equals("all", StringComparison.OrdinalIgnoreCase);
        bool isT   = token.Equals("t", StringComparison.OrdinalIgnoreCase);
        bool isCt  = token.Equals("ct", StringComparison.OrdinalIgnoreCase);
        bool isSlot = int.TryParse(token, out int wantedSlot);

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.IsBot || !p.PawnIsAlive)
                continue;

            if (isAll)
            {
                result.Add(p.Slot);
            }
            else if (isT)
            {
                if (p.Team == CsTeam.Terrorist) result.Add(p.Slot);
            }
            else if (isCt)
            {
                if (p.Team == CsTeam.CounterTerrorist) result.Add(p.Slot);
            }
            else if (isWeaponFilter)
            {
                if (GetActiveWeaponType(p) == wantedType) result.Add(p.Slot);
            }
            else if (isSlot)
            {
                if (p.Slot == wantedSlot) result.Add(p.Slot);
            }
            else
            {
                // treat as bot name (case-insensitive substring)
                if (!string.IsNullOrEmpty(p.PlayerName) &&
                    p.PlayerName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    result.Add(p.Slot);
            }
        }

        return result;
    }

    // Resolve any alive player (bot or human) by name or slot. Excludes spectators & HLTV.
    private static List<int> ResolvePlayer(string token)
    {
        var result = new List<int>();
        token = token.Trim();
        bool isSlot = int.TryParse(token, out int wantedSlot);

        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid || !p.PawnIsAlive || p.IsHLTV)
                continue;
            if ((int)p.TeamNum == 1)
                continue;

            if (isSlot)
            {
                if (p.Slot == wantedSlot) result.Add(p.Slot);
            }
            else
            {
                if (!string.IsNullOrEmpty(p.PlayerName) &&
                    p.PlayerName.Contains(token, StringComparison.OrdinalIgnoreCase))
                    result.Add(p.Slot);
            }
        }

        return result;
    }

    // Map a controller's active weapon to a CSWeaponType via the weapon's VData schema.
    private static CSWeaponType GetActiveWeaponType(CCSPlayerController controller)
    {
        var weapon = controller.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (weapon == null || !weapon.IsValid)
            return CSWeaponType.WEAPONTYPE_UNKNOWN;

        return weapon.As<CCSWeaponBase>().VData?.WeaponType ?? CSWeaponType.WEAPONTYPE_UNKNOWN;
    }

    private static bool TryParseWeaponType(string token, out CSWeaponType type)
    {
        switch (token.ToLowerInvariant())
        {
            case "pistol":      type = CSWeaponType.WEAPONTYPE_PISTOL;        return true;
            case "smg":         type = CSWeaponType.WEAPONTYPE_SUBMACHINEGUN; return true;
            case "rifle":       type = CSWeaponType.WEAPONTYPE_RIFLE;         return true;
            case "shotgun":     type = CSWeaponType.WEAPONTYPE_SHOTGUN;       return true;
            case "sniper":      type = CSWeaponType.WEAPONTYPE_SNIPER_RIFLE;  return true;
            case "machinegun":  type = CSWeaponType.WEAPONTYPE_MACHINEGUN;    return true;
            default:            type = CSWeaponType.WEAPONTYPE_UNKNOWN;       return false;
        }
    }

    private static bool TryParseRoute(string token, out RouteType route)
    {
        switch (token.ToLowerInvariant())
        {
            case "0": case "default": route = RouteType.Default; return true;
            case "1": case "fastest": route = RouteType.Fastest; return true;
            case "2": case "safest":  route = RouteType.Safest;  return true;
            case "3": case "retreat": route = RouteType.Retreat; return true;
            default: route = RouteType.Fastest; return false;
        }
    }
}
