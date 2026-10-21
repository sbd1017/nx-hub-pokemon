# Third-party software

The NX Hub trade bot is licensed under the **GNU Affero General Public License v3.0** (see
[LICENSE](LICENSE)) because it is built on SysBot.NET, which is AGPL-3.0. If you run a modified
copy for other people over a network, you must offer them its source code (the web page links to
it; set `NxHubPokemon:SourceUrl` to your copy).

| Component | Licence | How it is used |
| --- | --- | --- |
| [SysBot.NET](https://github.com/kwsch/SysBot.NET) by kwsch and contributors | AGPL-3.0 | Git submodule in `external/SysBot.NET`, compiled in unmodified: trade routines, queue, Discord integration. Release packages record the exact revision in `SYSBOT-NET-REVISION`. |
| [PKHeX.Core](https://github.com/kwsch/PKHeX) by kwsch and contributors | GPL-3.0 | NuGet package used by SysBot.NET: Pokémon data, legality checks. |
| PKHeX.Core.AutoMod ([PKHeX-Plugins](https://github.com/architdate/PKHeX-Plugins)) | MIT | Binary shipped inside SysBot.NET (`deps/`): makes legal Pokémon from Showdown sets. |
| [Discord.Net](https://github.com/discord-net/Discord.Net) | MIT | Used by SysBot.NET's Discord integration (optional). |
| [NLog](https://github.com/NLog/NLog) | BSD-3-Clause | Used by SysBot.NET. File logging is switched off here. |
| .NET runtime and ASP.NET Core | MIT | Self-contained in release packages. |

GPL-3.0 code may be combined with AGPL-3.0 code (both licences' section 13 allow it); the combined
program is distributed under AGPL-3.0.

[NX Hub](https://github.com/sbd1017/nx-hub) itself is a separate program under Apache-2.0. The
trade bot talks to it only over its HTTP API; no NX Hub code is included here.

"Pokémon" and "Nintendo Switch" are trademarks of Nintendo, Creatures and Game Freak. This project
is not affiliated with or endorsed by them.
