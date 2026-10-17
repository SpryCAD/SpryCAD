# SpryCAD

## Branches and pull requests

- Work on a feature branch, never directly on master: one branch per piece of work, merged by pull request
  (squash merge). Each push to master publishes a dev build, so master gets finished work only.
- Several sessions may work in this repository at once: work in your own git worktree, and never commit or revert
  changes you did not make.

## Change log

- End the pull request description with one `Changelog:` line per change users will notice, after a blank line. The
  squash merge makes the description the commit message on master, and those lines become the change log shown in
  the app (Help > Change Log), on the download page and in release notes (build/publish-r2.py reads master's history
  only, `--first-parent`, so lines in the branch's own commits are ignored). Write them for users: what they can now
  do, or what no longer goes wrong, in plain words, not how it was done. Internal work (refactors, tests, tools,
  docs, CI, speedups nobody would notice) gets none.

  ```
  Fillets: tangent chains solved together

  The technical description, as usual.

  Changelog: Fillets on tangent edge chains no longer fail
  Changelog: Fillet preview is faster on large parts
  ```
