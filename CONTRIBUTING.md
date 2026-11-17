# Contributing

Contributions are welcome. Please improve the original rather than starting a
separate copy: your work then reaches everyone who uses the app, and your name
is listed in CONTRIBUTORS.md.

## Ways to help

- Report a bug: what you did, what happened, and what you expected. Name your
  Windows version and, if it matters, the folder type (local drive, network
  share, archive).
- Suggest a feature. For a larger change, open an issue first, so that we agree
  on the approach before you spend time on it.
- Fix a bug or add a feature.

## Pull requests

1. Fork the repository and create a branch.
2. Build it. This needs the .NET 10 SDK:

   ```powershell
   dotnet build DirectoryFable.sln
   ```

3. Write code like the code around it. Test your change by hand in the running
   app, with test folders, not with your real files.
4. Open the pull request, and say what it changes and why.

## Credit

With your first merged contribution, add yourself to CONTRIBUTORS.md in the same
pull request, or ask and the maintainer adds you: your name or GitHub name, and
what you did. A bug report that led to a fix counts too.

## License of contributions

The program is licensed under the GNU General Public License, version 3, with the
additional terms in NOTICE.md. By sending a contribution, you agree that it is
licensed under the same terms, and you confirm that you have the right to
contribute it.
