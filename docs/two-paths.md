# The two paths a preview can be started from

Two releases in a row shipped the same bug twice: the file under the cursor is resolved by one piece of
code for a folder window and by another for the desktop, so a fix made for the folder view (`1.234.webp`
in 5.6.12) left the desktop broken until 5.6.14. This is the checklist that came out of it.

## Why there are two

| | Folder window | Desktop |
|---|---|---|
| How the item under the cursor is found | `IShellWindows` → `QueryActiveShellView` → `IFolderView` items | **not** a ShellWindows entry |
| What actually runs | that shell view | the two desktop folders (`DesktopDirectory` + `CommonDesktopDirectory`) |
| Why it is not unified | – | its shell browser is only reachable through `FindWindowSW(SWC_DESKTOP)`, which answered only about half the time here (the shell worker is an STA that does not pump); and `LVM_*` into Explorer's list view crosses a process with pointers in the message, which hangs |

So the *source* of the candidate names has to differ. What must not differ is the *matching*.

## What is shared, and where

- **Rules**: `MatchItemName` and `FindItemIndex` in
  `QuickLookNext/NativeMethods/QuickLook.cs`. Exact name, display name (extension hidden), details-view row,
  ellipsised label, name cut short, and the refusal to guess when several files share the visible start of a
  long name. Both paths call these.
- **Folder windows**: `ReadItemAtPointCore` in the same file.
- **Desktop**: `ReadDesktopItem`, also in the same file, reached through `IsDesktopRoot`.
- **Selection for the Space key**: one level up, `ReadSelectionCore` splits into `GetSelectionFromExplorer`
  and `GetSelectionFromDesktop` - a third pair with the same shape.

## Before shipping a change here

1. Rules only: `dotnet run --project QuickLook.Tests -c Release ItemNameMatch` (the shapes are pinned in
   `QuickLook.Tests/ItemNameMatchTests.cs`).
2. Folder window: `pwsh -NoProfile -File .\ql-smoke\verify-names-middle-click.ps1` (a folder window and a set
   of file names) or `verify-longname-middle-click.ps1` for one generated name.
3. Desktop: `pwsh -NoProfile -File .\ql-smoke\verify-desktop-names-middle-click.ps1` - it copies six
   tricky-named files onto the desktop, middle-clicks across the icon grid, checks every one of them against
   what the log reports and removes the files again.

The desktop script is the one that would have caught both regressions: it fails if a name that should
resolve does not, and the log line it prints (`hit="…" on the desktop in "…"`) says which name and which
folder the resolution used.
