---
name: release
description: Prepare a MeuralImageBackup release (release notes, version, README, dependency check), stopping before anything is published.
disable-model-invocation: true
---

# Release

## 1. Ask first, one question at a time

Use AskUserQuestion for each question, with the recommended option first and the questions numbered ("Question 2 of 5"). Never list questions in text, and never show a finished draft asking "OK?" instead.

1. **Release notes** as checkboxes (multiSelect). A question holds at most 4 options, so split them into New / Improved / Fixed, and offer "Minor fixes and updates."
2. **Version** (patch, minor or major, with the resulting number).
3. **README** as concrete choices: which bullets, table rows, sections and screenshots change. Screenshots use invented data only.

Write `CHANGELOG.md` and `README.md` only from the answers.

## 2. Do the work without further questions

- Check dependencies: NuGet packages, `dotnet-tools.json` (dotnet-ef), Docker base images, GitHub Actions. Bump patches and minors; ask before a major.
- Build, run the tests, and check the pages in Chrome.

## 3. Stop before anything is public

Pushing the `vX.Y.Z` tag publishes the image to ghcr.io and creates the GitHub release. Hand over a test instance (`docker compose up --build`) and wait for the user's OK. Commit, push, merge and tag each need their own approval.

The repo's local git config sets the geeForceOne identity, so a plain `git tag -a vX.Y.Z -m "Release X.Y.Z"` records the right tagger. Check with `git config user.name` before tagging.
