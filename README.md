# CS2-Bot-Nav
CS2 bot navigation read and control.

## Features

1. Set destinations for specific bots.
2. Read the short-term and long-term destinations of each bot.
3. Hold bots in place while keeping other behaviors active.
4. Read and set other relevant params.

## Commands

### Target Syntax

Applicable to all commands that take a `<target>` argument:

| Filter | Matches |
|-------|---------|
| `all` | All alive bots |
| `t` | All Terrorist bots |
| `ct` | All Counter-Terrorist bots |
| `<name>` | Bot name (case-insensitive substring) |
| `<slot>` | Bot by player slot number |
| `sniper` / `rifle` / `smg` / `machinegun` / `pistol` / `shotgun` | Bots by active weapon type |

### Movement Control

| Command | Usage | Description |
|---------|-------|-------------|
| `bot_nav_setgoal` | `<target> <x> <y> <z> [route]` | Set bot navigation goal to world position |
| `bot_nav_movetosite` | `<target> <0\|1>` | Set bot navigation goal to bombsite by index |
| `bot_nav_follow` | `<followers> <leader>` | Follower bots track leader's position in real time (leader: one player or bot) |
| `bot_nav_hold` | `<target>` | Hold bot around current position |
| `bot_nav_release` | `<target>` | Release bot back to state machine control |

- Route types (optional, default `fastest`): `0`/`default`, `1`/`fastest`, `2`/`safest`, `3`/`retreat`
- Bombsite Index (in most cases): `0`/`A Site`, `1`/`B Site`

### Read

| Command | Usage | Description |
|---------|-------|-------------|
| `bot_nav_goal` | `<target>` | Read the requested MoveTo destination and route |
| `bot_nav_site` | `<target>` | Read which bombsite the bot's goal and position are at |
| `bot_nav_path` | `<target>` | Read current path: index/length, next node (pos+area+traverse), endpoint |
| `bot_nav_task` | `<target>` | Read current task type, task entity, goal entity |

### Write

| Command | Usage | Description |
|---------|-------|-------------|
| `bot_nav_setroute` | `<target> <0-3>` | Change route style of active MoveTo |
| `bot_nav_settask` | `<target> <0-21>` | Write task type ([objective enum](https://github.com/ed0ard/CS2-Bot-Nav/blob/fcf7702c6b2e6c9aa83ad44db78e9321367e721d/NavTaskEntity.cs#L16-L37)) |

## Installation
1. Download the latest **BotNav.dll** from [Releases](https://github.com/ed0ard/CS2-Bot-Nav/releases).

2. Extract the folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server.

3. Restart your server.

## Cross-Plugin API

Other plugins can call every BotNav command's functionality directly with structured return values.

Consumers reference `BotNav.dll` at build time and call the API at runtime.

### Setup (Consumer Plugin)

**1. Add a `<Reference>` to `BotNav.dll` in your `.csproj`：**

```xml
<ItemGroup>
  <Reference Include="BotNav">
    <HintPath>..\..\BotNav\bin\Debug\net8.0\publish\BotNav.dll</HintPath>
    <Private>false</Private>
  </Reference>
</ItemGroup>
```

- `HintPath` — points to the BotNav build output (adjust relative path as needed).
- `<Private>false</Private>` — the DLL is **not** copied into your plugin folder. CSS loads BotNav from its own plugin directory at runtime.

**2. Use the API in your plugin：**

```csharp
using BotNav;

public class MyPlugin : BasePlugin
{
    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnAllPluginsLoaded>(() =>
        {
            var nav = BotNavPlugin.Instance;
            if (nav == null) return; // BotNav not loaded

            // Write
            nav.SetGoal(slot: 3, x: -100f, y: 200f, z: 0f, route: RouteType.Fastest);
            nav.MoveToSite(slot: 3, siteIndex: 0);     // bombsite by index (0 or 1)
            nav.Follow(slot: 5, leaderSlot: 2);
            nav.Hold(slot: 3);
            nav.Release(slot: 3);
            nav.SetRoute(slot: 3, route: RouteType.Fastest);
            nav.SetTask(slot: 3, taskType: 1);         // 1 = PLANT_BOMB

            // Read
            NavGoalData? goal = nav.GetGoal(slot: 3);
            NavSiteData? site = nav.GetSite(slot: 3);
            NavPathData? path = nav.GetPath(slot: 3);
            NavTaskData? task = nav.GetTask(slot: 3);

        });
    }
}
```

### API Reference

**Write**

| Method | Returns | Description |
|--------|---------|-------------|
| `SetGoal(int slot, float x, float y, float z, RouteType route)` | `void` | Set a single bot's nav goal to world position |
| `MoveToSite(int slot, int siteIndex)` | `bool` | Send bot to a random point inside a bombsite by index |
| `Follow(int slot, int leaderSlot)` | `void` | Make a single bot follow a leader player |
| `Hold(int slot)` | `void` | Hold a single bot around their current position |
| `Release(int slot)` | `bool` | Release a single bot. Returns `true` if it was controlled |
| `SetRoute(int slot, RouteType route)` | `bool` | Change route style of active MoveTo in place |
| `SetTask(int slot, int taskType)` | `bool` | Write task type ([objective enum](https://github.com/ed0ard/CS2-Bot-Nav/blob/fcf7702c6b2e6c9aa83ad44db78e9321367e721d/NavTaskEntity.cs#L16-L37), 0-21) |

**Read**

| Method | Returns | Description |
|--------|---------|-------------|
| `IsControlled(int slot)` | `bool` | Whether the bot is currently under BotNav movement control |
| `GetGoal(int slot)` | `NavGoalData?` | Read the requested MoveTo destination + route |
| `GetSite(int slot)` | `NavSiteData?` | Read which bombsite the goal / bot position is at |
| `GetPath(int slot)` | `NavPathData?` | Read current path index/length, next node, endpoint |
| `GetTask(int slot)` | `NavTaskData?` | Read current task type, task entity, goal entity |

- **"Controlled"** means the bot's navigation goal is being overridden every 0.1s by BotNav (via `SetGoal`, `Hold`, `Follow`, or `MoveToSite`). A bot is no longer controlled after `Release`, reaching their goal, leader death (Follow), or bot death.

**DTO Types**

```csharp
NavGoalData  { float X, Y, Z; RouteType Route; }

NavSiteData  { int? GoalSiteIndex; char GoalSiteLetter;
               int? StandingSiteIndex; char StandingSiteLetter;
               bool HasSites; }

NavPathData  { int Index; int Length; float CurrentX/Y/Z; bool HasPath;
               float NextX/Y/Z; int? NextAreaId; string? NextHow;
               float EndX/Y/Z; int? EndpointAreaId; }

NavTaskData  { int TaskType; string? TaskName;
               int? TaskEntityIndex; string? TaskEntityName;
               int? GoalEntityIndex; string? GoalEntityName; }
```
