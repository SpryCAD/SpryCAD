# SpryCAD

Parametric 3D CAD in C# (.NET 10) with a SolidWorks-style workflow: its own B-rep kernel (`src/SpryCad.Kernel`),
the model, regeneration, assemblies and file storage (`src/SpryCad.Core`), sheet metal (`src/SpryCad.SheetMetal.*`),
and the Avalonia app (`src/SpryCad.App`). See `README.md` for features and `docs/` for design notes and plans.

## Build and test

```
dotnet build SpryCAD.sln -c Debug
dotnet run --no-build -c Debug --project tests/SpryCad.Tests              # all tests
dotnet run --no-build -c Debug --project tests/SpryCad.Tests -- <filter>  # tests whose name contains <filter>
dotnet run --no-build -c Debug --project tests/SpryCad.SheetMetal.Tests
```

The test project references Kernel and Core, not the app. Changes to App strings can't break tests; changes to
Core messages can (see the copy style guide).

## Shared working tree

Several people and agent sessions work in this repository at the same time, often in the main checkout
(`C:\Users\kjacobson\dev\SpryCAD`) and often in the same files.

- **Check the branch before every commit** (`git branch --show-current`). Other sessions switch it without
  notice. If the main checkout isn't on the branch you mean, don't commit there.
- **Commit only your own changes.** The tree usually holds other sessions' uncommitted work, sometimes in the
  files you edited.
  - Go through `git diff HEAD` hunk by hunk.
  - When a file mixes your hunks with someone else's, stage a filtered patch (`git apply --cached --recount`).
    Build that patch from `git diff HEAD`, not plain `git diff`, because the shared index may hold others'
    staged hunks.
  - Commit whole files with explicit paths (`git commit -m … -- <paths>`), so nothing someone else staged gets
    swept in. Check `git show --stat HEAD` afterwards.
  - Don't leave things staged in the shared index. For a hand-built commit, use a private index
    (`GIT_INDEX_FILE=<scratch>/idx git read-tree HEAD`).
- **Don't reset, checkout or stash files you didn't change.** That throws away other sessions' work.
- **No AI attribution.** Don't add `Co-Authored-By: Claude …` or any other AI attribution line to commit messages
  or PR descriptions.

## Branches and merging

- **Feature work goes in a worktree.** Use a git worktree under `.claude/worktrees/<name>` (git ignores it) on
  its own branch, not the main checkout.
- **Features land with a squash merge:** `git merge --squash <branch>` on master, then one commit describing the
  feature. Don't push merge commits or the branch's intermediate commits onto master.
- Small fixes and copy changes can be committed directly on master, following the rules above.
- Commit messages follow the existing style: a plain description of what now happens and why, with
  before/after numbers for performance work.

## Writing

- **UI text** follows [docs/ui-copy-style.md](docs/ui-copy-style.md): short, technical, accurate. That covers
  status messages, tooltips, page notes, dialogs, menus, and Core error messages users see. Read it before adding
  or changing any user-facing string.
- **US spelling** everywhere: UI, comments, docs and commit messages (center, neighbor, color, gray, millimeter,
  miter, canceled). Older code still has some British spellings; fix them on lines you change anyway, not in
  sweeping edits of shared files.

## Verifying changes

- **Keep UI verification light:** build, and at most one quick look in the app when a visual change needs it.
  No long scripted click-throughs; the user reports problems. Run the relevant tests for Kernel, Core and
  file-format changes.
- **SpryCAD runs as a single instance.** Launching a build with a file while the user's own copy is open hands
  the file to their window. Before launching anything, run `Get-Process | ? ProcessName -like 'SpryCAD*'` on its
  own and wait for the result. If a copy is running, don't launch; say the change is untested in the app.
- **Scratch builds:** `bin\Release` is often locked by a running copy. Build to a scratch folder with
  `dotnet publish src/SpryCad.App -c Release -r win-x64 --self-contained false -o <dir>`; `dotnet build -o`
  leaves out the SQLite native DLLs, so Save fails. Check the folder has `e_sqlite3.dll` before handing it over.
- **Performance:** time it end to end in the real app, not only in the test harness. A ReadyToRun publish
  (`publish.ps1`) is faster than `bin\Release`. Check that runtime settings actually reached
  `runtimeconfig.json`.
