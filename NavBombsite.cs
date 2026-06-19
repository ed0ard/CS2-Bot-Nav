using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotNav;

// Which bombsite (A/B) a bot is targeting / standing in.
public partial class BotNavPlugin
{
    private void RegisterBombsiteCommands()
    {
        AddCommand("bot_nav_site",
            "Read which bombsite the bot's goal / position is at. Usage: bot_nav_site <target>", CmdNavSite);
        AddCommand("bot_nav_movetosite",
            "Send bot(s) to bombsite by index. Usage: bot_nav_movetosite <target> <0|1>", CmdNavMoveToSite);
    }

    private struct Bombsite { public Vector Center, Min, Max; public char Letter; }

    // Collect bombsite volumes and resolve their A/B letters from live state.
    private List<Bombsite> GetBombsites()
    {
        var sites = new List<Bombsite>();
        foreach (var e in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_bomb_target"))
        {
            if (e == null || !e.IsValid) continue;
            var col = e.Collision;
            var o = e.AbsOrigin;
            if (col == null || o == null) continue;
            Vector mn = new(o.X + col.Mins.X, o.Y + col.Mins.Y, o.Z + col.Mins.Z);
            Vector mx = new(o.X + col.Maxs.X, o.Y + col.Maxs.Y, o.Z + col.Maxs.Z);
            sites.Add(new Bombsite
            {
                Min = mn, Max = mx,
                Center = new Vector((mn.X + mx.X) / 2, (mn.Y + mx.Y) / 2, (mn.Z + mx.Z) / 2),
                Letter = '?',
            });
        }
        ResolveSiteLetters(sites);
        return sites;
    }

    private void ResolveSiteLetters(List<Bombsite> sites)
    {
        if (sites.Count == 0) return;

        // Authoritative once the bomb is down: CPlantedC4.m_nBombSite (0=A,1=B).
        foreach (var c4 in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("planted_c4"))
        {
            if (c4 == null || !c4.IsValid || c4.AbsOrigin == null) continue;
            int s = NearestSite(sites, c4.AbsOrigin);
            int n = TryGetSchemaInt(c4.Handle, "CPlantedC4", "m_nBombSite", -1);
            if (s >= 0 && n >= 0) sites[Set(sites, s, n == 1 ? 'B' : 'A')] = sites[s];
        }

        // Live pawns currently inside a bomb zone tell us the letter of that volume.
        foreach (var p in Utilities.GetPlayers())
        {
            var pawn = p?.PlayerPawn?.Value;
            if (pawn == null || !pawn.IsValid || pawn.AbsOrigin == null) continue;
            int wz = TryGetSchemaInt(pawn.Handle, "CCSPlayerPawn", "m_nWhichBombZone", 0);
            if (wz != 1 && wz != 2) continue;
            int s = SiteContaining(sites, pawn.AbsOrigin);
            if (s >= 0) Set(sites, s, wz == 2 ? 'B' : 'A');
        }

        // With two sites, one known letter implies the other.
        if (sites.Count == 2)
        {
            if (sites[0].Letter != '?' && sites[1].Letter == '?')
                Set(sites, 1, sites[0].Letter == 'A' ? 'B' : 'A');
            else if (sites[1].Letter != '?' && sites[0].Letter == '?')
                Set(sites, 0, sites[1].Letter == 'A' ? 'B' : 'A');
        }
    }

    private static int Set(List<Bombsite> sites, int i, char letter)
    {
        var s = sites[i]; s.Letter = letter; sites[i] = s; return i;
    }

    // Index of the site whose AABB contains p, else -1.
    private static int SiteContaining(List<Bombsite> sites, Vector p)
    {
        for (int i = 0; i < sites.Count; i++)
        {
            var s = sites[i];
            if (p.X >= s.Min.X && p.X <= s.Max.X &&
                p.Y >= s.Min.Y && p.Y <= s.Max.Y &&
                p.Z >= s.Min.Z && p.Z <= s.Max.Z) return i;
        }
        return -1;
    }

    // Site containing p, or the nearest site centre if p is outside both.
    private static int NearestSite(List<Bombsite> sites, Vector p)
    {
        int inside = SiteContaining(sites, p);
        if (inside >= 0) return inside;
        int best = -1; float bestD = float.MaxValue;
        for (int i = 0; i < sites.Count; i++)
        {
            var c = sites[i].Center;
            float d = (c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y) + (c.Z - p.Z) * (c.Z - p.Z);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // Always show the stable discovery-order index; append the A/B letter when
    // it has been calibrated (letter resolution can fail until a pawn/bomb is in a zone).
    private static string SiteLabel(List<Bombsite> sites, int i) =>
        i < 0 ? "none" : $"site {i} ({(sites[i].Letter != '?' ? sites[i].Letter.ToString() : "?")})";

    private static int TryGetSchemaInt(nint handle, string cls, string member, int fallback)
    {
        try { return Schema.GetSchemaValue<int>(handle, cls, member); }
        catch { return fallback; }
    }

    // bot_nav_site <target>
    private void CmdNavSite(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireMoveToReady(caller)) return;
        if (info.ArgCount < 2) { Reply(caller, "Usage: bot_nav_site <target>"); return; }

        var sites = GetBombsites();
        if (sites.Count == 0) { Reply(caller, "[BotNav] No func_bomb_target volumes on this map."); return; }

        var bots = ResolveBots(info.GetArg(1));
        if (bots.Count == 0) { Reply(caller, $"[BotNav] No matching bots for '{info.GetArg(1)}'."); return; }

        foreach (var (bot, c, pawn) in bots)
        {
            Vector goal = NavMem.ReadVec(bot.Handle, NavMem.MoveStateGoal);   // requested destination
            int goalSite = NearestSite(sites, goal);
            int hereSite = pawn.AbsOrigin != null ? SiteContaining(sites, pawn.AbsOrigin) : -1;
            string standing = hereSite >= 0 ? SiteLabel(sites, hereSite) : "(not in a site)";
            Reply(caller,
                $"[BotNav] {c.PlayerName}: goal heads to {SiteLabel(sites, goalSite)}  |  standing in {standing}");
        }
    }

    private static readonly Random _rng = new();

    // bot_nav_movetosite <target> <0|1>
    // The engine's letter comes from calibration (CPlantedC4.m_nBombSite 0=A/1=B, m_nWhichBombZone 1=A/2=B)
    // and is only known once a pawn/bomb has been in a zone; run bot_nav_site to see which
    // index is A vs B. A letter (A/B) is still accepted here when it is calibrated.
    // Each call picks a fresh RANDOM point inside the site, biased near its centre,
    // so repeated calls don't pile bots onto the exact same spot.
    private void CmdNavMoveToSite(CCSPlayerController? caller, CommandInfo info)
    {
        if (!RequireMoveToReady(caller)) return;
        if (info.ArgCount < 3) { Reply(caller, "Usage: bot_nav_movetosite <target> <0|1>  (0/1 = site index; bot_nav_site shows A/B)"); return; }

        var sites = GetBombsites();
        if (sites.Count == 0) { Reply(caller, "[BotNav] No func_bomb_target volumes on this map."); return; }

        string want = info.GetArg(2).Trim().ToUpperInvariant();
        int target = -1;
        if (int.TryParse(want, out int idx) && idx >= 0 && idx < sites.Count)
            target = idx;                                   // primary: stable site index 0/1
        else if (want is "A" or "B")
            target = sites.FindIndex(s => s.Letter == want[0]);  // optional: letter if calibrated
        if (target < 0)
        {
            Reply(caller, $"[BotNav] Could not resolve bombsite '{want}'. Use index 0..{sites.Count - 1} (letters need calibration).");
            return;
        }

        var site = sites[target];
        var bots = ResolveBots(info.GetArg(1));
        foreach (var (_, c, _) in bots)
        {
            Vector p = RandomPointInSite(site);             // fresh point per bot, near centre
            _controlled[c.Slot] = new ControlEntry { Hold = false, X = p.X, Y = p.Y, Z = p.Z, Route = RouteType.Fastest };
        }

        Reply(caller, bots.Count == 0
            ? $"[BotNav] No matching bots for '{info.GetArg(1)}'."
            : $"[BotNav] Sending {bots.Count} bot(s) to {SiteLabel(sites, target)} (random spots near centre).");
    }

    // A random point inside the site AABB, kept within 50% of each half-extent of the centre.
    private static Vector RandomPointInSite(Bombsite s)
    {
        float Jitter(float min, float max)
        {
            float half = (max - min) * 0.5f;
            float c = (max + min) * 0.5f;
            return c + ((float)_rng.NextDouble() * 2f - 1f) * half * 0.5f;
        }
        // Keep z near the centre; the bot's pathfinder snaps to the floor anyway.
        return new Vector(Jitter(s.Min.X, s.Max.X), Jitter(s.Min.Y, s.Max.Y), s.Center.Z);
    }
}
