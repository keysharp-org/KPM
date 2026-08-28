# KPM

The Keysharp package manager: a library, a CLI, and the ingestion bot for the
[Packages](https://github.com/keysharp-org/Packages) registry.

```
kpm add keysharp/findtext
```

```ahk
#Include <KPM/keysharp/findtext>

if FindText(&x, &y, 0, 0, A_ScreenWidth, A_ScreenHeight, 0, 0, pattern)
    Click(x, y)
```

## Design

**Artifacts are identified by their SHA-256, never by a URL.** A release manifest lists places the
same bytes can be fetched from, tried in order; an untrusted mirror cannot change what gets
installed, only whether the download succeeds. The registry can move hosts without invalidating a
single lockfile.

**Installing needs no token and no API.** The client fetches a static index and static release
assets over HTTPS. Rate limits and API outages are never a package manager's problem, and a stale
index still resolves — only discovering *new* versions needs the network.

**Packing is deterministic.** The same source tree produces byte-identical bytes on every platform,
so the registry re-packs a submission and requires the hash to reproduce. That is what lets anyone
verify an artifact without trusting whoever built it. It is also why this writes the ZIP container
itself: `ZipArchive` stamps each entry with the host platform, so its output differs between Windows
and Linux.

**Compatibility is earned.** A release declares which engines it runs on, and the registry's CI
compile-checks each claim. Most of the imported corpus is AutoHotkey code never checked against
Keysharp, so it declares `autohotkey` only and the Keysharp client filters it out rather than
offering a catalog that is mostly broken.

## Layout

| Project | What it is |
|---|---|
| `src/KPM.Core` | Everything: resolver, packer, registry client, installer, validation. No dependency on Keysharp. |
| `src/KPM.Cli` | `kpm` — a thin shell over KPM.Core. |
| `src/KPM.Bot` | `kpm-bot` — imports the existing AutoHotkey ecosystem into the registry. |
| `tests/KPM.Core.Tests` | |

`KPM.Core` is deliberately standalone: the CLI, the registry's CI, the bot and eventually a GUI are
all front ends over the same library, so a behaviour implemented in one is not reimplemented — or
left to drift — in the others.

## Commands

```
kpm add <owner/name>[@range]   add a dependency, then install
kpm install                    install exactly what kpm.lock.json names (works offline)
kpm update                     re-resolve within kpm.json's ranges
kpm search <text>              search the registry
kpm mirror                     download every artifact into the local cache
```

Registry maintenance, run inside the registry repository:

```
kpm manifest [dir]             regenerate a source-hosted package's release manifest
kpm validate [registry]        check every manifest, artifact hash and dependency
kpm index [registry]           build index.json.gz and catalog.json
kpm probe [dir] --out <dir>    lay out a project + probe script for an engine to compile-check
```

`kpm help` lists the rest.

## kpm.json and kpm.lock.json

```json
{ "schema": 1, "dependencies": { "keysharp/findtext": "^0.1" } }
```

The manifest holds ranges a human wrote; the lockfile holds the exact releases a resolve settled on,
each pinned by hash. `kpm install` restores the lockfile and nothing else, which is what makes a
rebuild reproducible and lets it work with no network at all.

Packages install to `Lib/KPM/<owner>/<name>/`, with a generated forwarder beside them so scripts
write `#Include <KPM/owner/name>` and never depend on a package's internal layout.

## Resolution

Highest version satisfying every range, no backtracking. When two packages want ranges that do not
overlap, KPM reports both requesters and stops rather than searching for older combinations that
might fit: for a registry of small script libraries the search almost never buys anything, and a
clear "these two disagree, pin one" beats a solver that silently downgrades a package you asked for
by name.

Prereleases are excluded from ranges, as in npm — except for a package whose releases are *all*
prereleases, which a good part of this corpus is. Excluding them there would make those packages
permanently unresolvable.

## Building

```
dotnet build KPM.slnx
dotnet test KPM.slnx
```
