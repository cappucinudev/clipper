# Clipper

Small Windows app to trim videos, shrink them to a target size, and make GIFs.

- **Video:** fit a size (8, 10, 25, 50, 100 MB or custom; two-pass x264 so it lands under the size),
  best quality (CRF), or trim only (no re-encode, instant). Resolution "Auto" picks a height that suits the bitrate.
- **GIF:** width, fps, colors, speed, dither, loop, boomerang. Uses ffmpeg palettegen/paletteuse.
- **Trim:** drag the handles, press `I` / `O` at the playhead, or type times. `Space` play, `P` play selection,
  `←`/`→` step 1 s (`Shift` for one frame), `Enter` export, `Ctrl+O` open.
- **Saving:** goes next to the original video. Pick another folder with "Change…" and Clipper remembers it
  until you click "↺ Video's folder". Never overwrites: adds " (2)" etc.

## Run

Needs ffmpeg: `winget install Gyan.FFmpeg`, or put `ffmpeg.exe` and `ffprobe.exe` next to `Clipper.exe`.

```powershell
.\build.ps1
.\dist\Clipper.exe
```

Open a video by dropping it on the window, dropping it on `Clipper.exe`, or "Open with" Clipper.
A second launch hands the file to the open window.

## How it works

`src/Clipper.cs` (C# 5, .NET Framework 4, built with the `csc.exe` in Windows, no packages) runs a
localhost HTTP server with a random token, runs ffmpeg/ffprobe, and opens `ui/index.html` in a slim
Edge app window. The page is embedded in the exe; a `ui/index.html` beside or one level above the exe
wins, so UI edits need no rebuild. Settings live in `%APPDATA%\Clipper\settings.json`.

Files dropped into the window have no path in the browser, so Clipper looks for the same name and size
in Downloads, Desktop, Videos, Documents, Pictures and OneDrive. If it can't find it, it copies the file
to `%TEMP%\Clipper` and saves to Videos.
