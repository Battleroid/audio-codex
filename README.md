# Audio Codex

A simple desktop tool to **browse, preview, and extract sounds** from Bungie's *Marathon*
(2025), in the spirit of [MIDA](https://github.com/DeltaDesigns/MIDA) and
[Deimos](https://github.com/cohaereo/Deimos-Public).

It reads the game's Tiger Engine `.pkg` packages directly (AES‑128‑GCM decryption +
Oodle/Kraken decompression), finds every Wwise audio file, and lets you play it,
view a waveform, inspect metadata, and export to WAV — individually or in bulk.

![overview](docs/overview.png)

## Features

- **Browse 58k+ sounds** across all packages, with instant search by tag id / package.
- **Group by Package or Soundbank** — soundbanks are reconstructed by parsing each Wwise
  bank's `HIRC` chunk and linking the WEM source ids it references (~93% of sounds get
  grouped under their owning bank).
- **Media player** — click a sound to **play immediately**; Space toggles play/pause;
  click the **waveform** to seek; volume slider.
- **List rows** show duration + a faint mini‑waveform; **multi‑select** for export.
- **Metadata** per sound: codec, channels, sample rate, duration, size, source package, tag id.
- **Export** the selected/multi‑selected sounds, the current filtered list, or everything, to WAV.

## Requirements

- **Windows x64** and a **.NET 8 runtime** (already present on most dev machines;
  otherwise `winget install Microsoft.DotNet.DesktopRuntime.8`).
- A local **Marathon install** — the tool uses the game's own
  `bin\x64\oo2core_9_win64.dll` (Oodle) to decompress packages. Nothing is redistributed.
- **vgmstream** for decoding Wwise audio — bundled in `tools/vgmstream/` and copied next
  to the app automatically.

## Running

```
Run.bat            # launches the Release build
```

or from source:

```
dotnet run --project src/App/MarathonAudio.App.csproj -c Release
```

## Setup / packaging

This tool is **C#/.NET** — it does **not** use Rust, so no Rust toolchain is required.

- **Fresh machine, from source:** `powershell -ExecutionPolicy Bypass -File scripts\setup.ps1`
  installs the .NET 8 SDK (via winget) if missing, downloads vgmstream if missing, and builds.
- **Standalone, no .NET install needed:** `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`
  produces a self‑contained build in `publish\` — zip that folder and it runs on any
  Windows x64 machine (the user still needs a Marathon install for the Oodle DLL).

On first launch, the game folder is auto‑detected at
`A:\Steam\steamapps\common\Marathon`. If yours is elsewhere, click **Browse…** and
pick the Marathon install folder (the one containing `packages\` and `bin\`).
Then click **Load sounds**.

## How it works

```
.pkg  ──►  parse header / entry table / block table        (Tiger/TigerPackage.cs)
      ──►  AES‑128‑GCM decrypt + Oodle (oo2core_9) inflate  (per 0x40000 block, on demand)
      ──►  Wwise WEM (RIFF/WAVE, codec 0xFFFF Vorbis)
      ──►  vgmstream  ──►  WAV  ──►  NAudio playback + waveform peaks
```

- Audio files are entries of **type 26 / subtype 7**; Wwise soundbanks are **26 / 6**.
- Packages are streamed block‑by‑block from disk, so indexing stays light on memory
  even though some packages are ~700 MB.

## Fonts

The UI uses the game's own typefaces — **PP Fraktion Mono** (body/data) and
**MarathonShapiro Wide 65** (titles) — for an in‑game look, the same fonts MIDA uses.
These are commercial fonts shipped with the game (font entries are type `24/0`, embedded
OTFs in the packages), so **they are not committed to this repo**. The app falls back to a
system font when they're absent. To restore the in‑game look, drop the OTFs into
`src/App/Assets/Fonts/` as `PPFraktionMono-Regular.otf`, `PPFraktionMono-Medium.otf`,
`PPFraktionMono-Bold.otf`, and `MarathonShapiroWide65.otf` (extract them from your own
Marathon install). Prebuilt release binaries embed them for convenience.

## A note on names

Retail Marathon **strips human‑readable sound names** — its Wwise soundbanks contain only
`BKHD` + `HIRC` chunks (no `STID` name tables), and the alpha‑era metadata tags MIDA used
are not present. So by default sounds are labelled by **tag id**, grouped under their
**soundbank** (`Bank XXXXXXXX`), with full audio metadata.

Each sound and bank still has its **Wwise id** (an FNV‑1 hash of the original name). Drop a
`wordlist.txt` next to the executable and the app reverses those ids MIDA‑style — every
match turns a `Bank ABCD…` / hash label into the real name. Without a wordlist, the ids
can't be reversed, so labels stay hash‑based (this is the same limitation MIDA has on retail).

## Project layout

```
src/Tiger/        Package reader, decryption, Oodle, vgmstream wrapper, WEM metadata, indexer
src/App/          Avalonia UI (browser, player, waveform, export)
src/Probe/        Console harness used to validate the pipeline
tools/vgmstream/  Bundled decoder (https://vgmstream.org)
```

Reference projects: **MIDA** (DeltaDesigns) and **Deimos** (cohaereo); package format
constants cross‑checked against the open‑source **tiger‑pkg** crate (v4nguard).
