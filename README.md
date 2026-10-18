# VibeVideo AI

**A Windows media player that finds the scenes you'd rather skip, and skips them.**

VibeVideo AI is [VibePlayer](https://github.com/MuhammadNoman76/VibePlayer) with an AI scene filter that runs on your own
PC. Open a video and it starts looking through it: scenes with nudity, sex or kissing turn pink on the progress bar, and
when playback gets there, the player jumps right over them. Every scene stays yours to change: keep it, stretch it, trim
it, or add one it missed. Nothing is uploaded, and the video file is never changed.

This is version **0.1.0, an early preview**. It can miss a scene, and it can mark a harmless one.

- Download page: https://www.nomanshafiq.com/vibevideo-ai/
- The story behind it, with measured speeds and what it gets wrong: https://www.nomanshafiq.com/blog/vibevideo-ai/

## Download

| Your Windows | Download | Size |
|---|---|---|
| **Windows 10 (version 1903 or later) / 11**, 64-bit (Intel or AMD, most PCs) | [VibeVideoAI-Setup-0.1.0-x64.exe](release/VibeVideoAI-Setup-0.1.0-x64.exe) | 82.8 MB |
| **Windows 11 / 10 on ARM** (Snapdragon and other ARM PCs) | [VibeVideoAI-Setup-0.1.0-arm64.exe](release/VibeVideoAI-Setup-0.1.0-arm64.exe) | 74.5 MB |

- There is no 32-bit or Windows 7/8.1 build: the AI runs through Windows ML, which only exists for 64-bit Windows 10 and 11.
  On those PCs, [VibePlayer](https://github.com/MuhammadNoman76/VibePlayer) gives you skip mode by hand.
- SHA-256 checksums are in [release/VibeVideoAI-SHA256SUMS.txt](release/VibeVideoAI-SHA256SUMS.txt). Check a download with
  `Get-FileHash .\VibeVideoAI-Setup-0.1.0-x64.exe` in PowerShell.
- The installers aren't code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click
  **More info › Run anyway**.
- Setup installs for all users (needs admin) or only for you, and can add VibeVideo AI to **Open with** for video and audio
  files, add **Play with VibeVideo AI** to the folder right-click menu, and create a desktop shortcut. It installs beside
  VibePlayer, never over it.

## How it finds scenes

1. **Look.** While a video plays, the app reads two pictures for every second of film with the same VLC engine that plays
   it, starting where you are watching. It stays ahead of you and fills in the rest afterwards.
2. **Score.** Each picture goes to **CLIP ViT-B/32**, OpenAI's image model, which compares it with 46 short descriptions:
   4 of kissing, 5 of sex scenes, 3 of nudity and 34 of ordinary scenes. A score is how strongly the picture matches one kind
   rather than all the others.
3. **Decide.** A scene needs two pictures over the line, or one far over it, so a single odd frame skips nothing. Scenes
   close together join up, and the edges move to the nearest cut, or get a little padding (2 s before, 1.5 s after).
4. **Skip.** Found scenes turn pink on the progress bar. When playback reaches one, it jumps to the end and says
   *"Skipped a scene (AI filter)"*.

Scans are saved per file in `%LOCALAPPDATA%\VibeVideoAI\scans`, so a film is scanned once; an edited file is scanned again.

## You stay in control

**Settings (gear) › AI scene filter** has:

- **Skip scenes the AI finds**: the main switch (off, the marks stay, dimmed, and everything plays).
- **Nudity and sex** and **Kissing**: what to skip.
- **Sensitivity**: Low (only clear scenes), Medium (default) or High (also borderline ones). No new scan needed.
- **Scan videos when they open**, **Processor** (Auto, NPU, GPU or CPU) and **Extra nudity detector (NudeNet)**, off by default.
- **Scenes to skip…**, also under *Skip scenes* and in the right-click menu: one list of every scene in the video, pink for
  the AI's and amber for your own, with times, length and whether it will be skipped. Select a scene to:
  - **stretch or trim it**: move its start or end a second at a time (−1 s / +1 s) or type the exact time;
  - **keep it** (*Don't skip*) or skip it again (*Skip it*);
  - **watch it once**, from three seconds before, without skipping it;
  - put an AI scene **back to the AI's times**, or **remove** one of yours;
  - **add a scene** the AI missed (it starts where you are watching). `[` and `]` while watching still work too.

Your changes are saved with the file's scan and find their scene again after a rescan, a sensitivity change or switching
the extra detector.

## Where it runs, and how fast

Through **Windows ML**, which fetches the right component for your chips from Microsoft (Intel OpenVINO on an Intel AI Boost
NPU, for example), the app uses the **NPU** first, then the best **graphics card** (DirectML), then the **CPU**.

Measured on my laptop (Intel Core Ultra 5 135U, 16 GB, Windows 11) with a 2:19 test reel, extra detector off:

| Chip | Speed | Per picture |
|---|---|---|
| NPU (Intel AI Boost) | 36× real time | 13 ms |
| GPU (Intel Graphics, DirectML) | 21× real time | 23 ms |
| CPU | 11× real time | 44 ms |

A 2-minute 1080p clip scanned at about 17× real time on the NPU: with full HD, decoding the pictures sets the pace.

## Privacy and downloads

- Videos are never uploaded, and the video file is never changed.
- The models aren't in the installer. The first scan downloads CLIP once from Hugging Face (176 MB in 16-bit for an NPU or
  graphics card, 352 MB in 32-bit for the CPU), and every file is checked against its size and SHA-256. NudeNet (10.6 MB)
  downloads only if you switch it on. After that, scans work offline.
- Updates: shortly after it starts and at most once a day, the app reads
  [`https://www.nomanshafiq.com/updates/vibevideo-ai.json`](https://www.nomanshafiq.com/updates/vibevideo-ai.json) with an
  ordinary HTTPS request; nothing else is sent. **Update now** downloads the installer for your edition, checks its SHA-256
  and installs it. Turn the check off in *Settings › Updates*.
- Uninstalling removes the downloaded models and saved scans; your settings stay in `%APPDATA%\VibeVideoAI`.

## Limits (it's a preview)

- It can mark a harmless scene: at High sensitivity it marked five seconds of a 1951 civil defence film as nudity or sex.
- It can miss a scene, especially a dark, quick or partly hidden one.
- It can only skip what it has scanned. If you jump far ahead, it starts scanning there, and a scene right at that point can
  play for a moment before it's found.
- It scans files on your PC, not network streams or audio files.
- It isn't parental control: anyone at the PC can switch it off.
- Tested on one Windows 11 laptop (Intel Core Ultra 5 135U: NPU, Intel graphics and CPU). Not tested yet: ARM64 hardware,
  AMD and Qualcomm NPUs, discrete NVIDIA or AMD graphics cards, and Windows 10.

## Everything VibePlayer does

- Press and hold the right half of the video (or the right mouse button) for 2x speed; let go to go back.
- YouTube-style controls: red progress bar with hover time, pill buttons, autoplay, captions, miniplayer and full screen.
- Skip mode for parts you mark yourself (amber on the progress bar): *Settings › Skip scenes*, or `[` and `]` while watching.
- Mouse wheel over the bar seeks ±5 s; anywhere else it changes the volume; Ctrl + wheel changes the brightness.
- Resumes long videos where you stopped, plays the rest of the folder, opens 47 video and 34 audio file types through LibVLC.

| Keys | Action |
|---|---|
| Space / K | Play / pause |
| J / L | Back / forward 10 s |
| ← / → (Ctrl: 1 min) | Back / forward 5 s |
| ↑ / ↓, mouse wheel | Volume |
| M | Mute |
| F, double-click, Alt+Enter | Full screen (Esc to leave) |
| 0–9, Home / End | Jump to 0–90 %, start / end |
| Shift+> / Shift+< | Faster / slower |
| . / , | Next / previous frame (paused) |
| C, V, B | Subtitles on/off, cycle subtitle, cycle audio track |
| [ / ] | Start / end a part you skip |
| I / T | Miniplayer / always on top |
| Shift+S | Snapshot |
| ? | Shortcut help |

## Build from source

Requirements: the [.NET 10 SDK](https://dot.net) and [Inno Setup 6](https://jrsoftware.org/isdl.php).

```powershell
powershell -ExecutionPolicy Bypass -File build-ai.ps1                  # x64 and ARM64 installers -> dist\
powershell -ExecutionPolicy Bypass -File build-ai.ps1 -Editions x64    # just one
```

Testing without a window:

```powershell
VibeVideoAI.exe --ai-scan movie.mp4 --ai-out report.json --ai-processor npu --ai-no-nudenet   # scan, write a JSON report
VibeVideoAI.exe --capture-scenes scenes.png                                                   # render the Scenes to skip window
```

## Project layout

```
src/VibePlayer/
  Core/        Player engine (LibVLC), playlist, settings, updater, single-instance pipe, compatibility helpers
  Core/Ai/     Scan data and the user's choices, scene finder, frame grabber, scanner, headless test
  Core/Ai/Onnx Windows ML device choice, model downloads, the CLIP / NudeNet analyzer (x64 and ARM64)
  Views/       Player UI, AI scene filter, Scenes to skip window, dialogs
  Assets/      Prompt embeddings (made by tools/ai/embed_prompts.py)
installer/     Inno Setup script (associations.iss is generated by build-ai.ps1)
release/       The installers and their SHA-256 checksums
```

## Licences

See [THIRD-PARTY-NOTICES.txt](src/VibePlayer/THIRD-PARTY-NOTICES.txt): LibVLC and LibVLCSharp (LGPL), ONNX Runtime (MIT),
CLIP (MIT, OpenAI) and NudeNet (AGPL-3.0, optional download). VibeVideo AI is not affiliated with or endorsed by VideoLAN
or OpenAI.
