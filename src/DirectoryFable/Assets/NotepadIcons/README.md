# Notepad# file icons

Pictures for the icons you can give a file in the Notepad# file list
(right-click a file, **Icon**). This folder can stay empty: every icon without
a picture shows its built-in symbol (`Editor/FileIcons.cs`).

To use your own pictures, put PNG files here and build DirectoryFable. They
are built into the exe (only `*.png` files are taken).

- Names: `<id>-<size>.png`, for example `star-16.png`.
- Sizes: 16, 20, 24, 32, 40, 48 and 64 pixels, square, with transparency. The
  app takes the smallest size that is at least as large as the screen needs.
- Ids: `star`, `heart`, `flag`, `bookmark`, `pin`, `check`, `warning`, `bug`,
  `idea`, `question`, `bolt`, `eye`, `gear`, `key`, `lock`, `code`, `terminal`,
  `folder`, `globe`, `book`, `calendar`, `clock`, `checklist`, `package`.
- Icons can be replaced one by one; the others keep their symbols.
