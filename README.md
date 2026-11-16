# any2hevc

Re-encodes a whole video library (Plex, Jellyfin, Emby, or plain folders) to HEVC (H.265, 8-bit Main profile, plays on every HEVC device), choosing **per file** the smallest size that still looks
like the source.

## How it picks the bitrate

For every file it:

1. Cuts 3–8 eight-second samples spread through the file (skipping intros and credits).
2. Binary-searches the encoder's quality setting (x265 CRF 14–32, NVENC CQ 16–38), test-encoding the samples each
   time and scoring them with **VMAF** against the original. It keeps the highest CRF/CQ (smallest file) whose
   average VMAF still reaches the target (default 95, hard to tell from the source on a TV). This takes about
   5 test encodes.
3. Predicts the final size from the samples. Every file is converted, even when it saves little, so the whole
   library ends up in one format. If the target quality would make the file *bigger* than the source (an
   already small rip), it instead uses the best quality that keeps it no larger, down to the bottom of the range
   (CRF 32 / CQ 38). Very heavily compressed sources can still grow slightly at that limit.
4. Encodes the whole file. Audio, subtitles, chapters and attachments are copied untouched.
5. Checks the result (HEVC, same length, same audio tracks), then swaps it into the library.

HEVC files made elsewhere (remuxes, web rips, 10-bit encodes) are converted too; untick *Re-encode HEVC files
from other sources too* to leave them alone. Every encode is tagged `ANY2HEVC` in the container, and files that
carry it (or `PLEXHEVC`, from when the app was called PlexHevc), or that the history lists as done, are never
re-encoded. AV1/VP9 files and clips under a
minute are skipped. HDR and Dolby Vision files are skipped too, because 8-bit output can't carry HDR. 10-bit SDR
sources are dithered down to 8-bit. Interlaced sources are deinterlaced with bwdif.

**Crop black bars** (on by default) removes letterbox / pillarbox bars from all four sides. cropdetect runs at 12
points through the file, and only rows and columns that are black at every point are cut, so dark scenes or
wider IMAX shots are never trimmed. Players add the bars back, and the smaller picture compresses better. The
list shows the new size, e.g. `1920x1080 → 1920x800`.

**Sharpen 5%** (off by default) applies a light unsharp mask (5x5, amount 0.05) to brightness only; sharpening
colour just adds fringes. It runs after deinterlacing and cropping. The VMAF reference is sharpened too, so the
quality search measures compression loss only. Sharpening raises VMAF on its own and would otherwise trick the
search into picking smaller, worse encodes.

**Dark scenes** (*Off* by default) protects shadows and night scenes. Banding and blocking show most there, and
VMAF under-rates damage there, so the quality search alone lets them slip.

- **Protect** gives dark, flat areas more bits. With x265 it uses `aq-mode=3` (adaptive quantization with a bias
  to dark scenes). With NVENC it raises `aq-strength` from 8 to 12, which favours flat areas, dark gradients
  included. Files come out somewhat bigger.
- **Strong** does the same, and the dark frames of the test samples (average brightness below 48 of 255) must
  reach the target VMAF on their own. They are scored a second time with their shadows brightened (gamma 2.0
  on both sides), so damage the eye sees once it has adjusted to a dark scene counts. On a night-time Blu-ray
  clip the dark frames scored 0.4–1.1 below the overall VMAF, so Strong picks a CQ/CRF about 2 lower for files
  with many dark scenes. Files that are mostly bright (dark frames under 5% of the samples) are unaffected. The
  log shows both scores, e.g. `CQ 21: VMAF 96.61, dark scenes 96.15`. Each test takes about twice as long, and
  the rule that a file never grows still applies. Raising NVENC's AQ further was tried and dropped: at
  `aq-strength` 15 files were 35–45% bigger for almost no gain in dark scenes.

## Encoder choice

| Encoder | Rough speed (Ryzen 9 5900X / RTX 5070 Ti) | Size at equal quality |
|---|---|---|
| x265, preset *slow* | ~15–25 fps 1080p, ~3–6 fps 4K | smallest |
| NVENC, *p7* | ~100–300 fps 1080p | roughly 10–20% larger |

x265 *slow* gives the best compression, but a large library can take weeks. NVENC is the practical choice when
there is a lot to get through.

## Originals

- **Replace, move originals to backup folder** (default): the backup folder mirrors the library structure. Play a
  few converted files, then free the space with *Delete backups…* (below).
- **Revert…** undoes it: every converted file whose original is in the backup folder is deleted, and the original
  is moved back to where it was (with its old name and extension). It shows the count and sizes and asks first.
  A converted file is only deleted when it is still exactly the file the app wrote and the backup is exactly the
  original's size. Anything else is left alone and logged. Watch mode is switched off first, so the originals
  aren't re-encoded straight away. The list is cleared afterwards; scan to rebuild it.
- **Delete backups…** is the opposite: it keeps the converted files and permanently deletes their originals from
  the backup folder to free the space. It asks twice. Only backups the history ties to a converted file that is
  still in the library, unchanged, are deleted, and each is checked again right before; anything else in the
  backup folder is left alone. Emptied backup folders are removed. Revert is no longer possible afterwards.
- **Replace, delete originals**: deletes each original after its HEVC copy is verified.
- **Write copies to output folder**: leaves the library alone. Good for a trial run.

MP4/M4V files stay MP4 (tagged `hvc1` so Apple devices play them). Every other container becomes MKV.

## Watch mode

Tick **Watch for new files** (next to the Stop buttons) and leave the app open. It watches the library folders, plus
a full re-check every 10 minutes for events that network shares drop. New files, and replaced ones such as an
upgraded release, are encoded automatically. A file is only picked up once it has stopped changing for a minute
and no other program has it open for writing, so downloads and copies still in progress are left alone. New
arrivals go ahead of the existing backlog, and the backlog is worked through as well.

*Stop* pauses watch mode until the next new file arrives; untick the box to switch it off. The setting is
remembered, so watch mode resumes when the app is started again. In *delete originals* mode it asks for
confirmation once, when you switch it on.

## Tray

The app has a tray icon whose badge shows what it is doing:

| Badge | Meaning |
|---|---|
| none | idle |
| blue dot | watching for new files |
| amber pause | watch mode paused (after *Stop*) until a new file arrives |
| spinning green segment | testing quality on a file |
| green pie, filling | encoding a file (the pie is its progress) |
| green tick / red ! | the queue finished while the window was hidden (with errors, for !); cleared when you open it |

Hover for the current file and percentage. **Closing the window hides it to the tray**: encoding and watch mode
carry on. Click the icon to open it again; right-click for Start, Stop after this file, Watch for new files and
**Exit**. Starting the app again while it's in the tray just brings the window back.

The app keeps an *any2hevc* shortcut in the Start menu, pointing at wherever the exe is. To pin it,
search for it in Start, right-click and choose *Pin to taskbar*, or right-click its taskbar button while it runs.
The pinned button opens the app, or brings it back from the tray.

Right-clicking the taskbar button (running or pinned) offers **Start encoding, Stop after this file, Stop now,
Scan library, Turn on/off watch mode** and **Exit**. They act on the running copy, even when it's hidden in the
tray. *Start encoding* on a closed app opens it, scans and starts. The stop items do nothing when it isn't running. When a queue finishes with
the window hidden, Windows shows a notification.

## Other details

- The status line at the bottom left shows the file count, how many are waiting, and how much space the
  re-encodes have saved so far.
- The list shows when each file finished encoding (*Finished*). Click the *File* or *Finished* header to sort
  (ascending, descending, then back to queue order). File names sort like Explorer, so E2 comes before E10.
- The queue runs biggest files first. *Stop after this file* and *Stop now* are safe: a stopped file keeps its
  original, and nothing half-written is left behind.
- Settings, results and the log are kept **next to the exe** (`settings.json`, `history.json`, `log.txt`), so the
  folder can be backed up or moved to another PC as a whole. Results make rescans quick, and no file is tested
  twice. Right-click a file to skip it or queue it again. If the exe's folder can't be written to (e.g. Program
  Files), they go to `%APPDATA%\any2hevc` instead. Older versions kept them in `%APPDATA%\PlexHevc`; those files
  are moved over on first start.
- With a Plex URL and token set, the app can ask Plex to rescan its libraries when the queue finishes.
- Set the `ANY2HEVC_DATA` environment variable (the old `PLEXHEVC_DATA` still works) to a folder to run a separate copy with its own settings and
  history, e.g. for testing.

## Running on another PC

`publish\any2hevc.exe` is all that's needed: copy it anywhere and run it. It contains the .NET runtime and
ffmpeg 7.0.1 (gyan.dev full build with libx265, hevc_nvenc and libvmaf, including the VMAF models). On first start
ffmpeg/ffprobe are unpacked once to `%LOCALAPPDATA%\any2hevc\ffmpeg` (about 280 MB, a couple of seconds). A new
version of the app replaces that copy; the old `%LOCALAPPDATA%\PlexHevc` copy is removed.

NVENC needs an NVIDIA GPU (GTX 10-series or newer) and its driver; on other PCs the app says so when *Start* is
pressed, so pick x265 there. Copy the whole folder (exe plus `settings.json` / `history.json`) to carry on where you
stopped. Keep the library on the same drive letter, since paths are stored in full. Even without history, files
the app already encoded carry an `ANY2HEVC` (or older `PLEXHEVC`) tag and are never encoded again.

To use a different ffmpeg, put `ffmpeg.exe` and `ffprobe.exe` next to `any2hevc.exe`; they take priority.

## Build

Put `ffmpeg.exe`, `ffprobe.exe` and ffmpeg's `LICENSE` in the `ffmpeg` folder (e.g. from `winget install
Gyan.FFmpeg`), or pass `-p:FFmpegDir=<folder>`. The build Brotli-compresses them into `obj\ffmpeg` (only when they
change) and embeds them in the exe (about 176 MB).

```bash
dotnet publish -c Release -o publish
```
