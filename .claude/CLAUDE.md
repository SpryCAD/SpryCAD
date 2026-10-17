# SpryCAD

## Branches and merging

- Work on a feature branch, never directly on master: one branch per piece of work, in your own git worktree.
- When the user has reviewed it and asks for it to be merged, squash merge it into the local master: one commit
  whose message describes the feature and ends with its `Changelog:` lines (below). No pull request. Don't push:
  the user pushes master. Each push to master publishes a dev build, so master gets finished work only.
- Bring the branch up to date with master first (merge master into it, build and run the tests), so the squash is
  the branch's tree. If the main checkout has another session's uncommitted changes in a file the merge touches,
  don't overwrite them: make the commit without the main checkout (`git commit-tree` of the branch's tree on master,
  then `git update-ref`), then bring the main checkout's other files up to date and apply your own hunks onto their
  copy of the shared file, so their uncommitted changes stay theirs.
- Several sessions may work in this repository at once: never commit or revert changes you did not make.

## Change log

- End the squash commit's message with one `Changelog:` line per change users will notice, after a blank line.
  Those lines become the change log shown in the app (Help > Change Log), on the download page and in release notes
  (build/publish-r2.py reads master's history only, `--first-parent`, so lines in the branch's own commits are
  ignored). Write them for users: what they can now
  do, or what no longer goes wrong, in plain words, not how it was done. Internal work (refactors, tests, tools,
  docs, CI, speedups nobody would notice) gets none.

  ```
  Fillets: tangent chains solved together

  The technical description, as usual.

  Changelog: Fillets on tangent edge chains no longer fail
  Changelog: Fillet preview is faster on large parts
  ```
