# Release checklist

What has to happen, in order, to publish the repository, the website and a release. The repository side is ready: license and notices, CI, the release and website workflows, the MCP Bundle manifest, the docs, the examples and the issue templates.

## 1. Before the repository goes public (once)

- [ ] Create an empty public repository on GitHub, with no README, license or `.gitignore`.
- [ ] Replace the placeholder `OWNER/ZeyoCut` with the repository's real path everywhere: `git grep -n "OWNER/ZeyoCut"` lists the files (README, CHANGELOG, `docs/`, `mkdocs.yml`, `packaging/manifest.json` and `.github/ISSUE_TEMPLATE/config.yml`).
- [ ] In [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), replace `[INSERT CONTACT METHOD]` with a way to reach the maintainers privately, such as a project email address. Don't use a personal account's address.
- [ ] Push the first commit, signed off (`git commit -s`).

## 2. Repository settings

- [ ] **About** (the gear on the repository's home page): a description, such as "A video and animation editor that an AI model drives, through MCP"; **Website** `https://cut.zeyoapp.com`; topics such as `mcp`, `video-editor`, `animation`, `windows`.
- [ ] **Settings → General → Features:** Issues on, Wiki off (the docs live in the repository).
- [ ] **Settings → Advanced Security:** turn on **Private vulnerability reporting** ([SECURITY.md](SECURITY.md) relies on it), **Dependabot alerts**, **Dependabot security updates**, **Secret scanning** and **Push protection**.
- [ ] **Settings → Actions → General → Workflow permissions:** "Read repository contents and packages permissions". The workflows ask for the rest themselves.
- [ ] **Settings → Rules → Rulesets:** a ruleset for `main` that requires a pull request, requires the status checks **Build and test** and **DCO sign-off**, and blocks force pushes and deletion.

## 3. Website (cut.zeyoapp.com)

- [ ] **Verify the domain** first, so nobody else can use it on GitHub Pages: in your account's (or organization's) **Settings → Pages**, click **Add a domain**, enter `zeyoapp.com`, add the TXT record GitHub shows to the DNS of zeyoapp.com, and click **Verify**.
- [ ] In the DNS of zeyoapp.com, add a CNAME record: `cut` → `<owner>.github.io` (the account or organization name, in lower case).
- [ ] **Settings → Pages:** set **Source** to **GitHub Actions**. Under **Custom domain** enter `cut.zeyoapp.com` and save; when the DNS check passes and the certificate is ready, tick **Enforce HTTPS**. (`docs/CNAME` keeps the domain with the site, but a site published by a workflow takes its domain from this setting.)
- [ ] **Actions → Website → Run workflow**, if it hasn't run since Pages was set up. Then open https://cut.zeyoapp.com and check the pages and the dark mode.

## 4. Every release

Versions follow [Semantic Versioning](https://semver.org/). Before 1.0, a new feature raises the minor version (0.2.0) and a fix the patch version (0.1.1).

- [ ] Set the version in `Directory.Build.props` (`<Version>`) and in `packaging/manifest.json` (`version`). The release workflow refuses a tag that doesn't match both.
- [ ] In [CHANGELOG.md](CHANGELOG.md), move the entries under `[Unreleased]` into a new `## [x.y.z] - YYYY-MM-DD` section dated the day of the release, and update the links at the bottom. For 0.1.0, check that its date is the release day.
- [ ] Check that CI passed on the commit to release.
- [ ] Tag it and push the tag:

  ```powershell
  git tag -a v0.1.0 -m "ZeyoCut 0.1.0"
  git push origin v0.1.0
  ```

- [ ] The **Release** workflow builds and tests on Windows, then publishes `ZeyoCut-<version>-win-x64.zip`, `ZeyoCut-<version>.mcpb` and `SHA256SUMS.txt` on a GitHub Release, with the notes from the CHANGELOG. Watch it under **Actions**.
- [ ] Test what was published, on a Windows computer:
  - [ ] Download the zip, the bundle and `SHA256SUMS.txt`, and check them as [docs/install.md](docs/install.md#check-the-download) shows.
  - [ ] Install the `.mcpb` in Claude Desktop, run `system_check`, open an example from [examples](examples/README.md) and export it.
  - [ ] Add the zip's exe to Claude Code with `claude mcp add` and check that `claude mcp list` shows it connected.
- [ ] If the docs changed, check that the website shows them.

## If a secret is ever committed

Revoke or rotate the secret first: once pushed, assume it has been copied. Then remove it from the history, which needs a force push and the owner's go-ahead, and ask GitHub Support to clear cached views if needed. Secret scanning with push protection (step 2) stops most of these before they are pushed.
