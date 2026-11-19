# Copyright and additional terms

DirectoryFable
Copyright (C) 2026 KintaroModding and the contributors listed in CONTRIBUTORS.md

This program is free software: you can redistribute it and/or modify it under
the terms of the GNU General Public License as published by the Free Software
Foundation, version 3 (see LICENSE), with the additional terms below.

This program is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the GNU General Public License for more details.

## Additional terms under section 7 of the GPL

These terms apply to this program and to every work based on it.

1. Credit (section 7b). Keep this file and CONTRIBUTORS.md with the source code.
   Where a work based on this program shows its legal notices, such as an About
   page, keep showing the line "Based on DirectoryFable by KintaroModding and
   contributors" with the address https://github.com/KintaroModding/directoryfable.

2. Marked as different (section 7c). A modified version must not claim to be the
   original or an official release. Give it a name of its own (see term 3), and
   state in its legal notices that it is a modified version.

3. Names (section 7e). The GPL grants no rights under trademark law to the names
   "DirectoryFable" and "KintaroModding". You may use them only to state
   truthfully where your work comes from.

## Libraries and credits

- AvalonEdit (MIT license), the text editor of Notepad#, used as a NuGet
  package. License text: licenses/AvalonEdit.txt.
- The Anthropic SDK for C# (Apache-2.0), GemmaChat's client for Claude's API,
  used as a NuGet package. License text: licenses/Anthropic.txt. It brings
  Microsoft.Extensions.AI.Abstractions and System.Net.ServerSentEvents (MIT
  license, parts of .NET).
- LibVLCSharp (LGPL-2.1-or-later), the .NET binding of VLC's engine for OpusHC, used as a NuGet package. License text: licenses/LibVLCSharp.txt.
  VLC's engine itself (libVLC, VideoLAN, GPL-2.0-or-later and LGPL-2.1-or-later)
  is not part of DirectoryFable: it is downloaded from VideoLAN on first use,
  together with its license (COPYING.txt in data\vlc\).
- The shell context menu code (src/DirectoryFable/Shell/ShellContextMenu.cs)
  follows the pattern of Vanara's ShellContextMenu (MIT license). License text:
  licenses/Vanara.txt.
- The NTFS file table reader of the search (src/DirectoryFable/Search/MftReader.cs)
  was written after the managed MFT parser of Lertaro (MIT license). License
  text: licenses/Lertaro.txt.
- The "Technical Notes" highlighting of Notepad# uses, unchanged, the TextMate
  grammar of the Technical Notes VS Code language mode (MIT license):
  src/DirectoryFable/Editor/Syntax/TechnicalNotes.tmLanguage.json. License text:
  licenses/TechnicalNotes.txt.
