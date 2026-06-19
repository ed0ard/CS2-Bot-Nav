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

Route types (optional, default `fastest`): `0`/`default`, `1`/`fastest`, `2`/`safest`, `3`/`retreat`

Bombsite Index (in most cases): `0`/`A Site`, `1`/`B Site`

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
| `bot_nav_settask` | `<target> <0-21>` | Write task type (objective enum) |

## Installation
1. Download the latest **BotNav.zip** from [Releases](https://github.com/ed0ard/CS2-Bot-Nav/releases).

2. Extract the folder and upload it to `game/csgo/addons/counterstrikesharp/plugins` on your server.

3. Restart your server.
