# SearchAnything

**Ctrl+F for anything on your screen.** Images, videos, scanned PDFs, screenshots, remote desktops, apps that won't let you select text: if you can see it, you can search it.

Press **Ctrl+Alt+F**. Your screen freezes, you type, and every match lights up.

- **Private.** Everything happens on your PC. SearchAnything never connects to the internet, and screenshots are never saved to disk.
- **Fast.** It reads a full screen in about 0.1 seconds, and results update as you type.
- **Free and lightweight.** It uses the text recognition built into Windows, so there are no AI models to download.
- **Multilingual.** Searches ignore accents and capital letters, so `accion` finds `Acción`. Every OCR language installed on your PC reads the screen at once, and their results are combined.

Want to check these claims? See [SECURITY.md](SECURITY.md).

## Get it

There are no prebuilt downloads. You build SearchAnything yourself from this source code, so you know exactly what runs on your PC. It takes about a minute: see [Build from source](#build-from-source), or [Install it](#install-it) to build it and add it to your Start menu in one step.

**Requirements:** Windows 10 (version 2004) or Windows 11, with at least one Windows language that includes text recognition. English and Spanish usually do.

## How to use

| Key | Action |
|---|---|
| **Ctrl+Alt+F** | Search the screen your mouse is on (press again to close) |
| Type | Highlight matches |
| **Enter** / **Shift+Enter** (or F3) | Next / previous match |
| **Ctrl+C** | Copy the line containing the current match |
| **Ctrl+Shift+C** | Copy all text on the screen |
| **Ctrl+Enter** | Search programs instead (see below) |
| **Esc** or click | Close |

Right-click the tray icon to **change the shortcut**, turn on **Start with Windows**, turn **Keep program list ready** on or off, choose the **Drives to scan for programs**, or exit.

### Find programs Windows lost track of

Reinstalled Windows on C: and your programs on D: or E: no longer show up in Start? They're still there; only the shortcuts pointing at them were lost.

When nothing on screen matches, a **Search programs** button appears in the bar. Press **Enter** (or click it) to search the programs on all your drives with the same text. **Ctrl+Enter** does it at any time.

| Key | Action |
|---|---|
| Type | Filter programs by name, file name or folder |
| **↑** / **↓** | Choose a program |
| **Tab** / **Shift+Tab** (or click a chip) | Show only the programs on one drive |
| **Enter** or click | Open it |
| **Ctrl+O** | Show it in its folder |
| **Ctrl+P** | Add it to the Start menu, so Windows Search finds it again on its own |
| **Ctrl+C** | Copy its path |
| **Esc** | Back to searching the screen |

Programs are named from their own version info, so `WINWORD.EXE` shows up as "Microsoft Word". Installers, uninstallers, updaters and crash reporters are left out, and a program's helper files fold into one row.

**Drives.** When matches are on more than one drive, chips above the list show how many are on each (`All 42 · C: 30 · D: 12`), and picking one narrows the list to that drive. To choose which drives are scanned at all, right-click the tray icon and open **Drives to scan for programs**. Internal drives are scanned unless you turn them off. USB sticks and memory cards are skipped until you turn them on, and network drives are never scanned. Turning a drive off forgets its programs right away.

**Memory.** The list lives in memory only, never on disk: about 2 MB for 2,400 programs. It's built in the background at low CPU and disk priority, starting 20 seconds after launch. A background scan waits while your PC's memory is more than 80% used, and the list is dropped completely above 90%. With **Keep program list ready** off, nothing is kept: programs are scanned when you search them and forgotten when the search closes.

Some programs may not start after a Windows reinstall, because they needed files or settings that were on the old C: drive. Reinstalling them into the same folder usually fixes that.

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
| [`ProgramSearch.cs`](src/SearchAnything/ProgramSearch.cs) | Which folders and .exe files count as programs, naming and ranking them |
| [`ProgramCatalog.cs`](src/SearchAnything/ProgramCatalog.cs) | Background scan of all drives, kept in memory, backing off when memory is tight |
| [`StartMenu.cs`](src/SearchAnything/StartMenu.cs) | Adds a program's shortcut to your Start menu (Ctrl+P) |
| [`TrayIcon.cs`](src/SearchAnything/TrayIcon.cs), [`HotkeyDialog.xaml(.cs)`](src/SearchAnything/HotkeyDialog.xaml.cs), [`Settings.cs`](src/SearchAnything/Settings.cs) | Tray menu, shortcut picker, saved settings |

SearchAnything writes only your settings (your shortcut and tray menu choices), in `%APPDATA%\SearchAnything\settings.json`. "Start with Windows" adds a value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, and **Ctrl+P** in program search adds a shortcut to your Start menu (`%APPDATA%\Microsoft\Windows\Start Menu\Programs`). Text you copy from a search is kept out of Windows clipboard history and the cloud clipboard.

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

### Install it

To keep it somewhere you'll find it again, run the install script from the repository folder:

```bash
powershell -ExecutionPolicy Bypass -File install.ps1
```

It runs the tests, builds the `.exe` for your PC (x64 or ARM), copies it to `%LOCALAPPDATA%\Programs\SearchAnything`, adds **SearchAnything** to your Start menu and starts it. It needs no administrator rights and changes nothing outside your user profile. Run it again after `git pull` to update.

To remove it (your settings in `%APPDATA%\SearchAnything` are kept):

```bash
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
```

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
