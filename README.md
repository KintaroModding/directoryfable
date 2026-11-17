# DirectoryFable

A two-pane file manager for Windows with a built-in text editor, Notepad#.
Dark, dense and made for the keyboard. Written in C# with WPF on .NET 10.

![DirectoryFable: two panes with tabs, folder tree and favorites bar](docs/screenshot-explorer.png)

## Status

There is no release yet: build it from source (see [Build](#build)). The first
release follows when the default settings are ready for everyone.

## Explorer

- **Two panes with tabs.** Each tab keeps its own folder, history, sort order
  and columns. `Ctrl+T` opens a tab, `Ctrl+W` closes it, `Ctrl+Shift+T` brings
  back a closed tab. Drag a tab to move it.
- **Folder tree and favorites bar**, shared by both panes; they act on the
  active pane. The tree shows Home, Favorites, Desktop and This PC. It follows
  the active pane, or stays where it is (toggle in its header). "Collapse all"
  goes back to the overview.
- **Address bar** with clickable parts. Type a path with `Ctrl+L`, `Alt+D` or
  `F4`; environment variables like `%APPDATA%` work.
- **Details view** with group dividers by file type. Sort, resize and reorder
  the columns. Hidden and system files are always shown.
- **Auto-refresh**: the lists and the tree follow changes on disk.
- **File operations through Windows** (progress window, conflict questions,
  Recycle Bin, elevation). **Undo** (`Ctrl+Z`) for copy, move, rename,
  duplicate and delete to the Recycle Bin.
- **Drag and drop** between the panes, tabs, the tree, the favorites and other
  programs. A right-button drag asks what to do; dropped into the same folder,
  it makes a copy under a new name.
- **The Windows context menu** in dark mode, so your shell extensions work.
- **Quick actions bar** in each pane: Folder path, File path, 7z (7-Zip's "Add
  to archive" window), Duplicate, Copy to other, Move to other, Terminal
  (Command Prompt or PowerShell), Explorer.
- **Archives as folders**: ZIP read and write; 7z and RAR read only, through an
  installed [7-Zip](https://www.7-zip.org/).
- **Properties window** with the details Windows knows for the file type (for
  example image size, artist, page count). `Alt+Enter` or the info button on a
  row.
- **Status bar**: selected and total folders, files and size; free space.
- **Settings page** behind the gear button. A normal window with a taskbar
  button (the default), or tray mode: only a tray icon, and closing the window
  hides it. Only one copy runs.
- **Session**: window, tabs, columns and the open Notepad# files come back at
  the next start.

## Notepad#

![Notepad#: file list with groups, editor and minimap](docs/screenshot-notepad.png)

- A second view in the title bar: `[folder] [Notepad#]`. Both views keep their
  state.
- **Open files in a vertical list**, in groups of your own and in automatic
  groups (by type or by folder). Drag files and groups to order them.
- **Editor** based on [AvalonEdit](https://github.com/icsharpcode/AvalonEdit):
  line numbers, syntax highlighting for the languages AvalonEdit knows plus
  AutoHotkey and INI, chosen per file, and word wrap (`Alt+Z`).
- **Search** (`Ctrl+F`) in a dark search box: match case, whole words, regular
  expressions.
- **Minimap** beside the scroll bar: click to jump, drag to scroll.
- **New files** (`Ctrl+N`) and **Save as** (`Ctrl+Alt+S`).
- **Nothing is lost on exit.** Unsaved changes and new files that were never
  saved come back at the next start, also after a crash (they are kept a few
  seconds after you stop typing). `Ctrl+Z` after a restart goes back to the
  saved text.
- **Changes from other programs**: a file without unsaved changes is reloaded
  by itself; with unsaved changes, Notepad# asks first.
- Files keep their encoding and line endings.
- **A file deleted outside stays in the list** in red and read-only, with its
  text, also after a restart. Save writes it back.
- **Open from the explorer** with the context menu, `Ctrl+E`, by dragging files
  onto the `[Notepad#]` tab or into Notepad#, or with a double-click on the file
  types you choose. The double-click works only inside DirectoryFable; the file
  associations of Windows do not change.

## Requirements

- Windows 11, x64. Windows 10 is not tested.
- To run: the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
- To build: the .NET 10 SDK.
- Optional: 7-Zip, for the 7z button and for 7z and RAR archives. It is found
  automatically, or set its folder on the settings page.

## Data folder

DirectoryFable is portable. It keeps its files in a `data` folder next to the
exe and writes nothing to AppData or the registry:

| File | Content |
|---|---|
| `settings.json` | Settings, with a comment for each key. The settings page writes it. |
| `favorites.json` | The favorites bar. |
| `session.json` | Window, tabs, columns and Notepad# files of the last run. |
| `notepad-cache\` | Notepad# texts kept over a restart: unsaved changes, new files, files deleted outside. |

## Command line

```text
DirectoryFable.exe [leftFolder] [rightFolder] [--multi] [--taskbar|--tray] [--data=<folder>]
```

- `leftFolder`, `rightFolder`: open the panes in these folders.
- `--multi`: start another copy, even when one is running.
- `--taskbar`, `--tray`: the window mode for this run.
- `--data=<folder>`: use another data folder.

## Keyboard

| Keys | Action |
|---|---|
| `Tab` | Switch the active pane |
| `Ctrl+T` / `Ctrl+W` / `Ctrl+Shift+T` | New tab / close tab / reopen a closed tab |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Alt+Left` / `Alt+Right` | Back / forward |
| `Alt+Up` or `Backspace` | Up one folder |
| `Ctrl+L`, `Alt+D`, `F4` | Type a path |
| `F5` | Refresh |
| `Ctrl+C` / `Ctrl+X` / `Ctrl+V` | Copy / cut / paste files |
| `F2` | Rename |
| `Del` / `Shift+Del` | To the Recycle Bin / delete for good |
| `Ctrl+Shift+N` | New folder |
| `Ctrl+Z` | Undo the last file operation |
| `Alt+Enter` | Properties window |
| `Ctrl+E` | Open the selected files in Notepad# |
| Notepad#: `Ctrl+N` / `Ctrl+O` / `Ctrl+W` | New / open / close a file |
| Notepad#: `Ctrl+S` / `Ctrl+Shift+S` / `Ctrl+Alt+S` | Save / save all / save as |
| Notepad#: `Ctrl+F`, then `F3` / `Shift+F3` | Search, next / previous match |
| Notepad#: `Alt+Z` | Word wrap on / off |
| Notepad#: `Ctrl+Tab` | Next file |

## Build

```powershell
dotnet build DirectoryFable.sln
.\build.ps1
```

`build.ps1` publishes one framework-dependent `DirectoryFable.exe` into `dist\`.

## Solution layout

```text
src/DirectoryFable/
  Views/           Windows, panes, bars, dialogs and the Notepad# view (XAML + code).
  ViewModels/      Tabs, panes, folder tree, favorites.
  Models/          File items and favorites.
  Infrastructure/  Settings, session, folder watchers, sorting, formatting.
  Shell/           Windows shell: icons, context menu, file operations, undo, archives, 7-Zip.
  Editor/          Notepad#: documents, groups, syntax highlighting, minimap.
  Themes/          Colors and control styles.
  Assets/          The app icon.
```

## Contributing

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md). Everyone who
contributes is named in [CONTRIBUTORS.md](CONTRIBUTORS.md).

## License

GNU General Public License, version 3 ([LICENSE](LICENSE)), with additional
terms under its section 7 ([NOTICE.md](NOTICE.md)): a work based on this program
keeps the credit to the original and its contributors, is marked as a modified
version with a name of its own, and gets no rights to the names "DirectoryFable"
and "KintaroModding". The licenses of the libraries are in
[licenses](licenses).
