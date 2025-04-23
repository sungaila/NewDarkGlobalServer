# ![NewDarkGlobalServer Logo](https://raw.githubusercontent.com/sungaila/NewDarkGlobalServer/master/Icon.png) NewDarkGlobalServer

[![GitHub Workflow Status (branch)](https://img.shields.io/github/actions/workflow/status/sungaila/NewDarkGlobalServer/dotnet.yml?branch=master&style=flat-square)](https://github.com/sungaila/NewDarkGlobalServer/actions/workflows/dotnet.yml)
[![GitHub license](https://img.shields.io/github/license/sungaila/NewDarkGlobalServer?style=flat-square)](https://github.com/sungaila/NewDarkGlobalServer/blob/master/LICENSE)
[![Docker Pulls](https://img.shields.io/docker/pulls/sungaila/newdarkglobalserver?style=flat-square)](https://hub.docker.com/r/sungaila/newdarkglobalserver)

A global server providing a game server list for [Thief 2](https://en.wikipedia.org/wiki/Thief_II) Multiplayer.

## How to setup Thief 2 Multiplayer
1. Get yourself a copy of the game (e.g. on [GOG.com](https://www.gog.com/de/game/thief_2_the_metal_age) or [Steam](https://store.steampowered.com/app/211740/Thief_II_The_Metal_Age/)).
2. Note: A few fan missions will not work after installing the multiplayer patch. So consider to create a copy of the Thief 2 game folder for singleplayer purposes.
3. Download the latest version of [T2Fix: An Unofficial Comprehensive Patch for Thief 2](https://github.com/Xanfre/T2Fix/releases).
4. Install T2Fix and make sure to select the component "Thief 2 Multiplayer".
5. Open the file `dark_net.cfg` in your game folder with a text editor.
6. Replace the last two lines with the following:
```ini
global_server_name thief2.sungaila.de
global_server_port 5199
```
6. Read the text file `mp_release_notes.txt` for more information.
7. Enjoy! Start the game with `Thief2MP.exe`. Windows might prompt you to install [DirectPlay](https://en.wikipedia.org/wiki/DirectPlay) on first launch.
    - Select `Multiplayer` and then `View Server List` to see all running game servers.
    - Host your own game with `Host a game` (UDP port `5198`). You will show up in the server list if clients can connect to you. If your game shows `Connected to global server.`, **but others cannot see you in the global server list**, then no connection can be established between your server and the clients (firewall, NAT, IPv4-only, other problems).
    - Sometimes selecting `View Server List` crashes the game. As a workaround try the following: Select `Host a Game`, then go `Back` and try again to select `View Server List`.
    - If viewing the server list is not working at all, you can fallback to using `Join a Game` and entering the server IP yourself.

<p align="center"><img src="https://raw.githubusercontent.com/sungaila/NewDarkGlobalServer/refs/heads/master/etc/GlobalServerList.png" width="600" alt="Screenshot of the global server list"></p>

## How to setup your own NewDarkGlobalServer
1. Download [the latest release](https://github.com/sungaila/NewDarkGlobalServer/releases).
2. Launch `NewDarkGlobalServer.exe`.
    - Default TCP port is `5199`
    - You can change that port with the command line argument `--port=YOURNUMBER`
    - Note: IPv4 is the only supported protocol (IPv6 is not)
3. Make sure all your clients have updated their `dark_net.cfg` file with your name and port.

## Command-line options
Use the `--help` argument to show all available options.
```
Usage: NewDarkGlobalServer [options]
Starts a server providing a game server list for Thief 2 Multiplayer.

Options:
  -p, --port=VALUE           Sets the port for this global server. Default is
                               5199.
  -s, --timeoutserver=VALUE  Sets timeout for game servers in seconds. Default
                               is 180 seconds (00:03:00).
  -c, --timeoutclient=VALUE  Sets timeout for game clients in seconds. Default
                               is 3600 seconds (01:00:00).
  -u, --timeoutunidentified=VALUE
                             Sets timeout for connections to indentify as
                               client or server in seconds. Default is 10
                               seconds (00:00:10).
  -b, --showheartbeatminimal Shows HeartbeatMinimal messages in the log. Each
                               connected game server sends one every 10 seconds
                               so the log may become cluttered.
  -f, --hidefailedconn       Hides failed connections attempts (due to invalid
                               or unknown messages) from the log.
  -t, --printtimestamps      Adds timestamps to the log output.
  -v, --verbose              Shows more verbose messages in the log.
  -h, --help                 Prints this helpful option list and exits.
```
