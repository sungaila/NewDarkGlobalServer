# ![NewDarkGlobalServer Logo](https://raw.githubusercontent.com/sungaila/NewDarkGlobalServer/master/Icon.png) NewDarkGlobalServer

[![GitHub Workflow Status (branch)](https://img.shields.io/github/workflow/status/sungaila/NewDarkGlobalServer/Build%20project/master?style=flat-square)](https://github.com/sungaila/NewDarkGlobalServer/actions/workflows/dotnet.yml)
[![GitHub license](https://img.shields.io/github/license/sungaila/NewDarkGlobalServer?style=flat-square)](https://github.com/sungaila/NewDarkGlobalServer/blob/master/LICENSE)

A global server providing a game server list for [Thief 2](https://en.wikipedia.org/wiki/Thief_II) Multiplayer.

## How to setup Thief 2 Multiplayer
1. Get yourself a copy of the game (e.g. on [GOG.com](https://www.gog.com/de/game/thief_2_the_metal_age) or [Steam](https://store.steampowered.com/app/211740/Thief_II_The_Metal_Age/))
2. Download the latest version of [T2Fix: An Unofficial Comprehensive Patch for Thief 2](https://github.com/Xanfre/T2Fix/releases)
3. Install T2Fix and make sure to select the component "Thief 2 Multiplayer"
4. Open the file `dark_net.cfg` in your game folder with a text editor
5. Replace the last two lines with the following:
```ini
global_server_name thief2.sungaila.de
global_server_port 5199
```
6. Enjoy! Start the game with `Thief2MP.exe`. Windows might prompt you to install [DirectPlay](https://en.wikipedia.org/wiki/DirectPlay) on first launch.
    - Select `Multiplayer` and then `View Server List` to see all running game servers.
    - Host your own game with `Host a game` (UDP port `5198`). You will show up in the server list **if clients can ping you** (firewall, NAT, IPv4-only ...).

## How to setup your own NewDarkGlobalServer
1. Download [the latest release](https://github.com/sungaila/NewDarkGlobalServer/releases)
2. Launch `NewDarkGlobalServer.exe`
    - Default TCP port is `5199`
    - You can change that port with the command line argument `-port=YOURNUMBER`
    - IPv4 is the only supported protocol
3. Make sure all your clients have updated their `dark_net.cfg` file with your name and port
