#!/usr/bin/env bash
# Build and publish a Windows release of Shattered Paradise (ranked build).
#
# Usage: ranked/release.sh <version> [--dry-run]
#
#   <version>   ranked-X.Y.Z, e.g. ranked-0.1.0 (also used as the git tag)
#   --dry-run   set version, build and create the zip, then restore mods/sp/mod.yaml;
#               never commits, tags or pushes
#
# Output: dist/ShatteredParadise-<version>-win64.zip
# Non-dry-run: commits mods/sp/mod.yaml ("Release <version>"), creates an annotated tag,
# pushes branch + tag to the remote "fork" and creates a GitHub release (gh) or prints
# the manual upload steps.
set -euo pipefail

REMOTE=fork
GH_REPO=almshadi/Shattered-Paradise-sdk

usage() { sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'; exit "${1:-1}"; }

VERSION=""
DRY_RUN=0
for arg in "$@"; do
	case "$arg" in
		--dry-run) DRY_RUN=1 ;;
		-h|--help) usage 0 ;;
		-*) echo "Unknown option: $arg" >&2; usage ;;
		*) [ -z "$VERSION" ] || { echo "Only one version allowed" >&2; usage; }; VERSION="$arg" ;;
	esac
done
[ -n "$VERSION" ] || usage
if ! [[ "$VERSION" =~ ^ranked-[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
	echo "Invalid version '$VERSION' - expected ranked-X.Y.Z (e.g. ranked-0.1.0)" >&2
	exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
MOD_YAML="mods/sp/mod.yaml"
LAUNCHER="ranked/launcher"
NAME="ShatteredParadise-$VERSION"
DIST="$ROOT/dist"
ZIP="$DIST/$NAME-win64.zip"
BUILD_LOG="$DIST/build-$VERSION.log"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "==> Release $VERSION$([ "$DRY_RUN" = 1 ] && echo ' (dry run)')"

# --- Preconditions -------------------------------------------------------------
echo "==> Checking prerequisites"
for tool in git make rsync python3 dotnet; do
	command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done
for f in "$MOD_YAML" "$LAUNCHER/play.cmd" "$LAUNCHER/README.txt"; do
	[ -f "$f" ] || { echo "Missing file: $f" >&2; exit 1; }
done

echo "==> Checking working tree"
if ! git diff --quiet HEAD -- || ! git diff --cached --quiet --; then
	if [ "$DRY_RUN" = 1 ]; then
		echo "    (warning: uncommitted changes to tracked files - dry run continues with the working tree)"
		git status --short --untracked-files=no | sed 's/^/      /'
	else
		echo "Uncommitted changes to tracked files - commit or stash them first:" >&2
		git status --short --untracked-files=no >&2
		exit 1
	fi
fi
BRANCH="$(git symbolic-ref --short -q HEAD || true)"
[ -n "$BRANCH" ] || { echo "Detached HEAD - check out a branch first" >&2; exit 1; }

if git rev-parse -q --verify "refs/tags/$VERSION" >/dev/null; then
	echo "Tag $VERSION already exists locally" >&2
	exit 1
fi
if remote_tags="$(git ls-remote --tags "$REMOTE" "refs/tags/$VERSION" 2>/dev/null)"; then
	[ -z "$remote_tags" ] || { echo "Tag $VERSION already exists on remote $REMOTE" >&2; exit 1; }
else
	[ "$DRY_RUN" = 1 ] || { echo "Cannot reach remote $REMOTE to check tags" >&2; exit 1; }
	echo "    (warning: could not reach remote $REMOTE to check tags)"
fi

# --- Version -------------------------------------------------------------------
# Restore mod.yaml on exit for dry runs, and on failure before the release commit.
WORK="$(mktemp -d)"
cp -p "$MOD_YAML" "$WORK/mod.yaml.orig"
RESTORE=1
cleanup() {
	if [ "$RESTORE" = 1 ] && [ -f "$WORK/mod.yaml.orig" ]; then
		cp -p "$WORK/mod.yaml.orig" "$ROOT/$MOD_YAML"
		echo "==> Restored $MOD_YAML"
	fi
	rm -rf "$WORK"
}
trap cleanup EXIT

echo "==> Setting version to $VERSION"
# mod.yaml is CRLF; set_mod_version's awk patterns ("...: User$") don't match before a \r,
# so convert to LF, set the version, then convert the whole file back to CRLF.
sed -i 's/\r$//' "$MOD_YAML"
make version VERSION="$VERSION" >/dev/null
sed -i 's/\r$//; s/$/\r/' "$MOD_YAML"
grep -E '^\s*Version:|: User\s*$' "$MOD_YAML" | sed 's/\r$//; s/^/    /'
grep -q "Version: $VERSION"$'\r'"\$" "$MOD_YAML" || { echo "Version line not updated" >&2; exit 1; }
grep -q "/$VERSION: User"$'\r'"\$" "$MOD_YAML" || { echo "SupportDir line not updated" >&2; exit 1; }

# --- Build ---------------------------------------------------------------------
echo "==> Building (win-x64), log: $BUILD_LOG"
mkdir -p "$DIST"
if ! make TARGETPLATFORM=win-x64 >"$BUILD_LOG" 2>&1; then
	grep -E "error|Error" "$BUILD_LOG" | grep -v " 0 Error" | tail -30 || true
	echo "Build FAILED - full log: $BUILD_LOG" >&2
	exit 1
fi
grep -E "^\s+[0-9]+ Error|Build succeeded" "$BUILD_LOG" | tail -2 || true
[ -f engine/bin/OpenRA.dll ] || { echo "engine/bin/OpenRA.dll missing after build" >&2; exit 1; }

# --- Package -------------------------------------------------------------------
echo "==> Staging $NAME/"
STAGE="$WORK/$NAME"
mkdir -p "$STAGE"
rsync -a \
	--exclude '/.git' --exclude '/.github' --exclude '/.vscode' \
	--exclude '/ranked/' --exclude '/dist/' --exclude '/support/' --exclude '/workspace/' \
	--exclude 'engine/OpenRA.*/obj' --exclude 'OpenRA.Mods.Sp/obj' \
	--exclude '*.log' --exclude '/next-step.txt' --exclude '/user.config' --exclude '/win.sh' \
	"$ROOT/" "$STAGE/"
cp "$LAUNCHER/play.cmd" "$STAGE/play.cmd"
sed "s/{VERSION}/$VERSION/g" "$LAUNCHER/README.txt" >"$STAGE/README.txt"

echo "==> Creating $ZIP"
rm -f "$ZIP"
python3 -I - "$WORK" "$NAME" "$ZIP" <<-'PY'
	import os, sys, zipfile
	base, name, out = sys.argv[1:4]
	with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
	    for root, dirs, files in os.walk(os.path.join(base, name)):
	        dirs.sort()
	        rel = os.path.relpath(root, base)
	        z.write(root, rel)
	        for f in sorted(files):
	            p = os.path.join(root, f)
	            if os.path.islink(p) and not os.path.exists(p):
	                continue
	            z.write(p, os.path.join(rel, f))
	PY

echo "    Size: $(du -h "$ZIP" | cut -f1)"
echo "    Top level:"
python3 -I - "$ZIP" <<-'PY'
	import sys, zipfile
	names = zipfile.ZipFile(sys.argv[1]).namelist()
	top = sorted({"/".join(n.split("/")[:2]) for n in names if n.count("/") >= 1 and n.split("/")[1]})
	for t in top:
	    print("      " + t)
	PY

if [ "$DRY_RUN" = 1 ]; then
	echo "==> Dry run complete: $ZIP (mod.yaml will be restored, nothing committed)"
	exit 0
fi

# --- Commit, tag, push -----------------------------------------------------------
echo "==> Committing $MOD_YAML"
git add "$MOD_YAML"
git commit -q -m "Release $VERSION"
RESTORE=0

echo "==> Tagging $VERSION"
git tag -a "$VERSION" -m "Shattered Paradise $VERSION"

echo "==> Pushing $BRANCH and $VERSION to $REMOTE"
git push "$REMOTE" "$BRANCH"
git push "$REMOTE" "refs/tags/$VERSION"

# --- GitHub release ------------------------------------------------------------
TITLE="Shattered Paradise $VERSION"
NOTES="Windows build of Shattered Paradise with ranked matchmaking.

1. Install the .NET Desktop Runtime 6.0 (x64): https://dotnet.microsoft.com/download/dotnet/6.0
2. Download $NAME-win64.zip and unzip it anywhere.
3. Run play.cmd. On first start the game downloads the Tiberian Sun content.
4. Click Ranked in the main menu."

if command -v gh >/dev/null; then
	echo "==> Creating GitHub release"
	gh release create "$VERSION" "$ZIP" --repo "$GH_REPO" --title "$TITLE" --notes "$NOTES"
else
	cat <<-EOF
	==> gh not installed - create the GitHub release manually:
	    1. Open https://github.com/$GH_REPO/releases/new?tag=$VERSION
	    2. Tag: $VERSION (already pushed)
	       Title: $TITLE
	    3. Upload: $ZIP
	    4. Notes:
	$(printf '%s\n' "$NOTES" | sed 's/^/       /')
	    5. Publish release
	EOF
fi

echo "==> Done: $VERSION"
