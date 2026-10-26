# Publish ADM automatically on GitHub

The included `.github/workflows/release.yml` runs on every push to `main` or `master`, and can also be started manually from the Actions tab with either branch selected.

Your current repository, `Anjana2k02/ADM-Download-Manager`, uses **master**. To activate the corrected workflow, commit and push the changes on that branch:

```powershell
git add .github/workflows/release.yml GITHUB-RELEASES.md
git commit -m "Fix automatic releases for master branch"
git push origin master
```

Then open https://github.com/Anjana2k02/ADM-Download-Manager/actions and wait for the release workflow. The installer appears under Releases after every step succeeds. A manually created release with only source-code archives has not built the application.

It tests the software, builds the two ZIP packages and a Windows installer, checks installation/launch/uninstall on the runner, computes SHA-256 checksums, and then creates a GitHub Release marked **Latest**. Failed tests or packaging stop publication. Every published run keeps its own release history. Only the release job has repository write permission; it uses GitHub's automatically provided token, so no personal access token secret is required.

## First-time setup

1. Sign in to GitHub and create a **public** repository named `ADM` (or another name you prefer). Keep it empty: do not initialize it with a README, license, or gitignore yet. Public visibility lets your community download releases without repository access.
2. Open PowerShell in this project folder and run these commands, replacing `YOUR-USERNAME` and the repository name as needed:

```powershell
git init -b main
git add .
git commit -m "Prepare ADM and automatic Windows releases"
git remote add origin https://github.com/YOUR-USERNAME/ADM.git
git push -u origin main
```

Git may ask you to sign in through its credential manager. If it asks for your commit identity, configure `git config user.name "Your Name"` and `git config user.email "your GitHub commit email"`, then retry the commit. Do not paste a token into tracked files or the remote URL.

3. Open the repository's **Actions** tab. The **Build and release ADM** workflow should start automatically. Enable Actions if GitHub prompts you.
4. After all jobs turn green, open **Releases** on the repository page. The installer and ZIP files appear under **Assets**. They are release attachments; the large binaries are not committed to Git.

If Actions is restricted, check **Settings → Actions → General** and allow the official GitHub actions used by the workflow. The workflow requests `contents: write` for its release job; organization policies or tag rules may need the repository owner to allow that operation. Do not grant unrelated permissions.

## Future updates

After changing and testing ADM:

```powershell
git add .
git commit -m "Describe the update"
git push origin main
```

The next successful run publishes a new latest release. For example, workflow run 42, attempt 1 becomes **1.0.42.1** and tag **v1.0.42.1**. Retrying it creates **1.0.42.2**, so the workflow never overwrites a published release or moves its tag. The executable's version, installer version, and app footer use the generated version.

Every push to main triggers the release workflow, including documentation changes. Develop unfinished work on another branch, then merge to main when ready. GitHub may coalesce pending workflow runs during rapid pushes. Publishing is serialized, and a run skips publication if main has advanced, so an older build cannot knowingly replace the newest commit's release. Older published releases remain available.

## Share one permanent link

Replace `YOUR-USERNAME/ADM` with your repository:

```text
Release page:
https://github.com/YOUR-USERNAME/ADM/releases/latest

Direct installer download:
https://github.com/YOUR-USERNAME/ADM/releases/latest/download/ADM-Setup-win-x64.exe

Portable ZIP with bundled runtime:
https://github.com/YOUR-USERNAME/ADM/releases/latest/download/ADM-standalone-win-x64.zip
```

These links lead to the latest published release without needing a new URL for each version. The installation is per Windows user, under `%LocalAppData%\Programs\ADM`, with a Start menu shortcut and an optional desktop shortcut. Running a newer installer upgrades that installation. Uninstalling removes installed program files, preserving the queue and downloads. The application does **not** yet check for updates or install them automatically; users download a newer installer from this page.

## Local builds and verification

```powershell
.\scripts\Build.ps1 -Standalone -Version 1.0.42.1
.\scripts\Build-Installer.ps1 -Version 1.0.42.1
```

Installer compilation requires [Inno Setup 6](https://jrsoftware.org/isdl.php). The GitHub `windows-2025` runner currently includes it. The script checks that installer and executable versions match. The installer includes the runtime and optional browser-helper files; it does not automatically register the browser extension.

For a downloaded file, compare its hash with the release's `SHA256SUMS.txt`:

```powershell
Get-FileHash .\ADM-Setup-win-x64.exe -Algorithm SHA256
```

Releases are unsigned until you add a code-signing certificate/service. GitHub hosting does not digitally sign the executable. Choose a license before inviting others to reuse or contribute to the source; making a repository public does not itself grant an open-source license.

## Troubleshooting

- **No run after push:** confirm the branch is `main` and `.github/workflows/release.yml` is committed.
- **Tests/build fail:** open the failed step in Actions, correct the error, then push again. The current public release stays available.
- **Inno Setup missing:** the runner image may have changed; install Inno Setup on the runner or point `Build-Installer.ps1` to the compiler. It intentionally fails instead of publishing an incomplete installer.
- **Release permission error:** check organization policies, repository Actions settings, and tag rules. The standard workflow already requests release-write permission.
- **No public download:** verify the repository is public and the release job completed. Workflow artifacts alone are not the community download page.

References: [GitHub .NET workflows](https://docs.github.com/en/actions/tutorials/build-and-test-code/net), [GitHub CLI release creation](https://cli.github.com/manual/gh_release_create), [Windows runner software](https://github.com/actions/runner-images/blob/main/images/windows/Windows2025-Readme.md).
