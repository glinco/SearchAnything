# SearchAnything

**Ctrl+F for anything on your screen.** Images, videos, scanned PDFs, screenshots, remote desktops, apps that won't let you select text: if you can see it, you can search it.

Press **Ctrl+Alt+F**. Your screen freezes, you type, and every match lights up.

- **Private.** Everything happens on your PC. SearchAnything never connects to the internet, and screenshots are never saved to disk.
- **Fast.** It reads a full screen in about 0.1 seconds, and results update as you type.
- **Free and lightweight.** It uses the text recognition built into Windows, so there are no AI models to download.
- **Multilingual.** Searches ignore accents and capital letters, so `accion` finds `Acción`. Every OCR language installed on your PC reads the screen at once, and their results are combined.

Want to check these claims? See [SECURITY.md](SECURITY.md).

## Get it

There are no prebuilt downloads. You build SearchAnything yourself from this source code, so you know exactly what runs on your PC. It takes about a minute: see [Build from source](#build-from-source).

**Requirements:** Windows 10 (version 2004) or Windows 11, with at least one Windows language that includes text recognition. English and Spanish usually do.

## How to use

| Key | Action |
|---|---|
| **Ctrl+Alt+F** | Search the screen your mouse is on (press again to close) |
| Type | Highlight matches |
| **Enter** / **Shift+Enter** (or F3) | Next / previous match |
| **Ctrl+C** | Copy the line containing the current match |
| **Ctrl+Shift+C** | Copy all text on the screen |
| **Esc** or click | Close |

Right-click the tray icon to **change the shortcut**, turn on **Start with Windows**, or exit.

## How it works

```
Ctrl+Alt+F ─► screenshot of the monitor under the mouse
           ─► Windows OCR finds every word and its position, once per installed language, in parallel
           ─► each keystroke searches those words in memory (instant)
           ─► matching areas are highlighted on the frozen screenshot
```

**Why several languages?** OCR engines make different mistakes. Measured on the same screen, the Spanish engine read accents correctly but turned `15/10/2026` into `15/18/2826`, while the English engine got the digits right but turned `Acción` into `Acci6n`. SearchAnything searches every reading and shows each spot once.

**Number searches are forgiving.** When your search contains a digit, stray spaces are ignored (OCR likes to read `$1,240.50` as `$1, 240.50`), and a letter O inside a number-like word counts as zero (`2O26`, or the `o` in a counter like `o / 29`). Ordinary words such as "TOPWORD" are left alone.

| File | Role |
|---|---|
| [`App.xaml.cs`](src/SearchAnything/App.xaml.cs) | Startup, tray, hotkey wiring, single-instance check |
| [`HotkeyManager.cs`](src/SearchAnything/HotkeyManager.cs) | System-wide shortcut |
| [`ScreenCapture.cs`](src/SearchAnything/ScreenCapture.cs) | Screenshot of the monitor under the mouse |
| [`OcrService.cs`](src/SearchAnything/OcrService.cs) | Windows OCR → words with pixel positions, one engine per language |
| [`SearchIndex.cs`](src/SearchAnything/SearchIndex.cs) | Accent/case-insensitive matching on one reading → highlight boxes |
| [`CombinedIndex.cs`](src/SearchAnything/CombinedIndex.cs) | Searches all readings and merges hits on the same spot |
| [`OverlayWindow.xaml(.cs)`](src/SearchAnything/OverlayWindow.xaml.cs) | The full-screen search UI |
| [`TrayIcon.cs`](src/SearchAnything/TrayIcon.cs), [`HotkeyDialog.xaml(.cs)`](src/SearchAnything/HotkeyDialog.xaml.cs), [`Settings.cs`](src/SearchAnything/Settings.cs) | Tray menu, shortcut picker, saved settings |

The only thing written to disk is your shortcut, in `%APPDATA%\SearchAnything\settings.json`. "Start with Windows" adds a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Text you copy from a search is kept out of Windows clipboard history and the cloud clipboard.

## Known limitations

- **DRM video shows up black.** Netflix and other protected content blocks screenshots, so there's nothing to read.
- **Text only, for now.** It finds words, not objects ("dog", "red car"). See the roadmap.
- **Highlights inside a word are estimated.** Searching part of a word places the box assuming letters of similar width.
- **One monitor at a time**, whichever one the mouse is on.
- **Stylized or tiny text** (logos, handwriting, very small fonts) may not be recognized.
- **Some misreads can't be undone.** In small monospace fonts, OCR has read `50` as `se`. If no engine gets a word right, it can't be found.

## Build from source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```bash
dotnet test SearchAnything.slnx
dotnet run --project src/SearchAnything
```

To build a single `.exe` you can keep and run anywhere (use `-r win-arm64` on ARM PCs such as Snapdragon or Surface Pro X):

```bash
dotnet publish src/SearchAnything -c Release -r win-x64 -o publish
```

There's nothing to install. Run `publish\SearchAnything.exe` and it waits in the system tray (the yellow magnifying glass).

Developer self-test, which runs OCR on an image and writes timings, the recognized text and matches to a new file (it never reads your screen, and it won't overwrite an existing file):

```bash
SearchAnything.exe --selftest report.txt image.png [query]
```

## Roadmap

- [ ] Search for objects and concepts ("dog", "chart"), using a small local AI model
- [ ] Live mode, where highlights follow scrolling and video
- [ ] Search across all monitors
- [ ] Android app

Ideas and pull requests are welcome. Please open an issue first for big changes.

## License

[MIT](LICENSE)
