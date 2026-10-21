<h1 align="center">NX Hub trade bot</h1>

<p align="center">
  <strong>A self-hosted Pokémon trade bot for your own Switch, with a web page instead of Discord commands.</strong><br>
  Built on SysBot.NET · runs on a Raspberry Pi · works with or without NX Hub
</p>

People ask for a Pokémon on a web page (paste a Showdown set or upload a file), join the queue, and
get a link code. When their turn comes, the bot opens a link trade on your Switch and sends it.
There is also a **trainer info** request: the bot opens a link trade, reads the trainer details
from a Pokémon you show it (name, TID, SID, gender, game, language), and backs out without trading
anything. Useful for making Pokémon with your own trainer details.

Under the hood this is [SysBot.NET](https://github.com/kwsch/SysBot.NET), the trade bot many
Discord servers run, with a web front end added. Same trade routines, same legality checks
(PKHeX + Auto-Legality Mod), same `config.json`. If you already run SysBot.NET with Discord, import
your `config.json` and the Discord bot keeps working alongside the web page.

## Games

Whatever SysBot.NET supports: **Scarlet/Violet, Legends: Z-A, Sword/Shield, Brilliant
Diamond/Shining Pearl and Legends: Arceus**. Pick one in the web page; changing it restarts the
service.

## What you need

- A Switch with custom firmware (Atmosphère) and [sys-botbase](https://github.com/olliz0r/sys-botbase),
  on the same network as the computer running the bot.
- The game open, saved, and set up the way SysBot.NET expects (see its wiki). An online
  subscription for link trades.
- A Raspberry Pi 5 (or any 64-bit Linux, macOS or Windows machine).

> [!WARNING]
> Link trades take the Switch online while custom firmware and sys-botbase are running. Nintendo
> bans consoles it detects doing that, and bot hosts do get banned. Only run a trade bot on a console
> you are prepared to lose online access on.

## Install on a Raspberry Pi

```bash
git clone --recurse-submodules https://github.com/sbd1017/nx-hub-pokemon.git && cd nx-hub-pokemon
scripts/package.sh linux-arm64                          # on your computer (.NET 10 SDK)
scp artifacts/nxhub-pokemon-*-linux-arm64.tar.gz pi@raspberrypi.local:

tar -xzf nxhub-pokemon-*-linux-arm64.tar.gz && cd nxhub-pokemon-*-linux-arm64    # on the Pi
sudo ./install.sh
sudo cat /var/lib/nxhub-pokemon/admin-token
```

Open `http://<pi>.local:8820`, sign in with the token, choose the game and your Switch's IP address
under **Setup**, then press **Start** when the game is ready. The bot never starts by itself unless
you set `NxHubPokemon:StartBotsOnLaunch`.

### With NX Hub

If [NX Hub](https://github.com/sbd1017/nx-hub) also controls the Switch, link the two so NX Hub
steps aside while the bot runs (otherwise both would press buttons at once):

```bash
sudo ./install.sh --nxhub http://127.0.0.1:8810 --switch-id sw_abc123 --nxhub-token-file /var/lib/nxhub/admin-token
```

To link the two pages, set `NxHub:Ui:Links` in NX Hub (name it `Trade bot` so its "Handed over"
notice links here too) and `NxHubPokemon:NxHub:PageUrl` here.

Pressing **Start** then asks NX Hub to hand the Switch over (NX Hub releases its inputs, disconnects
and shows "Handed over"). **Stop** hands it back. If the bot crashes, NX Hub takes the Switch back
after a minute on its own.

## Run from source

```bash
git submodule update --init
dotnet run --project src/NxHub.Pokemon        # http://localhost:8820, token in src/NxHub.Pokemon/data/admin-token
dotnet test
```

## Settings

`appsettings.json` (or environment variables such as `NxHubPokemon__Auth__Token`):

| Setting | Default | |
| --- | --- | --- |
| `NxHubPokemon:DataDirectory` | systemd state directory, else `./data` | `config.json`, admin token, request history, dumps |
| `NxHubPokemon:StartBotsOnLaunch` | `false` | Start trading as soon as the service starts |
| `NxHubPokemon:PathBase` | | Sub-path behind a reverse proxy, for example `/nxhub/trade` |
| `NxHubPokemon:Auth:Mode` | `Token` | `Disabled` only on a trusted network |
| `NxHubPokemon:Auth:TrustedProxyUserHeader` | | Header a reverse proxy on the same machine sets to the signed-in user |
| `NxHubPokemon:Auth:Owners` | everyone | Proxy users who may start/stop the bot and change setup |
| `NxHubPokemon:NxHub:*` | | Hand-over: `BaseUrl`, `SwitchId`, `Token` or `TokenFile`, `LeaseSeconds`; `PageUrl` adds a link back to NX Hub |
| `NxHubPokemon:SourceUrl` | this repository | Linked from the page, as AGPL-3.0 requires |

Everything about trading itself (codes, timings, legality rules, Discord) is SysBot.NET's
`config.json`; see the SysBot.NET wiki.

## API

All under `/api`, with `Authorization: Bearer <token>`:

| | |
| --- | --- |
| `GET /status` | Game, bots, queue length, hand-over state |
| `POST /requests/trade` | `{set}` (Showdown set) or `{file}` (Base64 Pokémon file), optional `code` |
| `POST /requests/trainer-info` | Optional `code` |
| `GET /requests` · `DELETE /requests/{id}` | Your requests, with progress messages and trainer details; cancel a queued one |
| `POST /bot/start` · `POST /bot/stop` | Owner only |
| `GET/PUT /setup` · `POST /setup/import` | Owner only: game and Switch, or import a SysBot.NET `config.json` |
| `GET /queue` · `GET /log` | Owner only |

## Licence

AGPL-3.0, because SysBot.NET is. See [THIRD-PARTY.md](THIRD-PARTY.md). Not affiliated with
Nintendo, The Pokémon Company or Game Freak.
