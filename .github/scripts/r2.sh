#!/usr/bin/env bash
# The Cloudflare R2 legs of the Linux and macOS release workflows (build-linux.yml,
# build-macos.yml). The website 302s the big downloads (installers, feed *.nupkg) to a public R2
# bucket on its Cloudflare hosts, while the direct bss.* hosts keep streaming the box's copy, so a
# release lands in BOTH places: this script is the bucket half, the workflows' ssh/docker cp legs
# are the box half. The manifests (RELEASES, releases.*.json, assets.*.json) stay box-only.
#
#   r2.sh configured                      exit 0 when the four R2 values are set, 1 when none are
#                                         (the R2 legs are skipped), 2 when only some are (an error)
#   r2.sh put <file> <key> <cache-control> [content-disposition]
#                                         uploads one file (S3 PUT, atomic: no .incoming dance)
#   r2.sh prune                           the bucket pass of the release-feed prune, see below
#
# Environment: R2_ACCOUNT_ID, R2_BUCKET, AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY (a bucket-scoped
# Object Read and Write token, CI's own, not the server's). Uploads go to the account's S3
# endpoint, https://<R2_ACCOUNT_ID>.r2.cloudflarestorage.com, NEVER to a Cloudflare-proxied host,
# whose 100 MB request-body cap would cut a full package short. The repo is public: nothing here
# echoes a secret, and GitHub masks them in the log regardless.
#
# The AWS CLI preinstalled on both runner images is used. Since early 2025 the AWS CLI sends CRC32
# checksum trailers by default, which R2 rejects, so both checksum settings are pinned to
# WHEN_REQUIRED below.
#
# Seams for running this off CI: AWS_CMD (default `aws`) replaces the CLI, R2_PRUNE_DRY_RUN=1
# prints the delete list without deleting.
#
# Written for bash 3.2 and BSD userland as well as GNU (the macOS runner), so: no arrays, no
# mapfile, no xargs -d, no sort -V.

set -u

AWS_CMD="${AWS_CMD:-aws}"
export AWS_REQUEST_CHECKSUM_CALCULATION=WHEN_REQUIRED
export AWS_RESPONSE_CHECKSUM_VALIDATION=WHEN_REQUIRED
export AWS_DEFAULT_REGION=auto
NL=$'\n'
RELEASES_PREFIX="downloads/releases/"

r2() {
  ${AWS_CMD} --endpoint-url "https://${R2_ACCOUNT_ID}.r2.cloudflarestorage.com" --region auto "$@"
}

cmd_configured() {
  set_count=0
  for v in R2_ACCOUNT_ID R2_BUCKET AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY; do
    eval "val=\${$v:-}"
    if [ -n "${val}" ]; then set_count=$((set_count + 1)); fi
  done
  if [ "${set_count}" -eq 4 ]; then
    echo "R2 configured: bucket uploads and the bucket prune pass will run"
    return 0
  fi
  if [ "${set_count}" -eq 0 ]; then
    echo "R2 not configured (no R2_* secrets): the bucket legs are skipped, the box legs run as before"
    return 1
  fi
  echo "::error::R2 is half configured (${set_count} of R2_ACCOUNT_ID, R2_BUCKET, R2_ACCESS_KEY_ID, R2_SECRET_ACCESS_KEY are set). Set all four or none."
  return 2
}

cmd_put() {
  file="$1"; key="$2"; cache="$3"; disposition="${4:-}"
  if [ ! -f "${file}" ]; then
    echo "::error::r2 put: no such file ${file}"
    return 1
  fi
  if [ -n "${disposition}" ]; then
    r2 s3 cp "${file}" "s3://${R2_BUCKET}/${key}" --only-show-errors --no-progress \
      --content-type application/octet-stream --cache-control "${cache}" --content-disposition "${disposition}" || return 1
  else
    r2 s3 cp "${file}" "s3://${R2_BUCKET}/${key}" --only-show-errors --no-progress \
      --content-type application/octet-stream --cache-control "${cache}" || return 1
  fi
  echo "uploaded to R2: ${key}"
}

# The bucket pass of the release-feed prune. The keep rule is the box pass's, line for line
# (feed-referenced names from FEED_B64, plus the KEEP_VERSIONS newest versions per channel, and
# only fully-parsed *.nupkg names are ever deleted), so the two copies of the feed never drift. The
# one textual difference is the version sort: `sort -V` is GNU, so this sorts the dotted numeric
# fields with -n keys instead, which orders every name classify() accepts (digits and dots only,
# yyyy.Mdd.r) exactly as -V does.
cmd_prune() {
  KEEP_N="${KEEP_VERSIONS:-3}"
  echo "== prune release feed: R2 bucket pass =="
  echo "prefix: ${RELEASES_PREFIX}   policy: keep feed-referenced + the ${KEEP_N} newest versions per channel"

  feed=$(printf '%s' "${FEED_B64:-}" | base64 --decode 2>/dev/null | sed '/^$/d')
  if [ -z "${feed}" ]; then
    echo "::warning::no feed-referenced assets resolved, refusing to prune the bucket (a fetch failure must never look like an empty feed)"
    return 0
  fi

  raw=$(r2 s3 ls "s3://${R2_BUCKET}/${RELEASES_PREFIX}") || {
    echo "::warning::could not list the bucket's ${RELEASES_PREFIX}, nothing pruned there"
    return 0
  }
  # `aws s3 ls` prints "<date> <time> <size> <name>" per object and "PRE <dir>/" per sub-prefix.
  # Feed names never contain spaces, so the last field is the name.
  listing=$(printf '%s\n' "${raw}" | tr -d '\r' | awk '$1 != "PRE" && NF >= 4 { print $NF }' | sed '/^$/d')
  if [ -z "${listing}" ]; then
    echo "the bucket holds no release files, nothing to prune."
    return 0
  fi

  in_list() {
    case "${NL}$2${NL}" in *"${NL}$1${NL}"*) return 0 ;; esac
    return 1
  }

  # Identical to the box pass's classify(): see build-linux.yml for why an unsuffixed name is win.
  classify() {
    CH=""; VER=""
    case "$1" in .*) return 1 ;; esac
    case "$1" in *.nupkg) ;; *) return 1 ;; esac
    stem=${1%.nupkg}
    case "${stem}" in
      typebeat-*-full)  stem=${stem%-full} ;;
      typebeat-*-delta) stem=${stem%-delta} ;;
      *) return 1 ;;
    esac
    rest=${stem#typebeat-}
    case "${rest}" in
      *-win)   CH=win;   VER=${rest%-win} ;;
      *-linux) CH=linux; VER=${rest%-linux} ;;
      *-osx)   CH=osx;   VER=${rest%-osx} ;;
      *)       CH=win;   VER=${rest} ;;
    esac
    case "${VER}" in ""|*[!0-9.]*) return 1 ;; esac
    return 0
  }

  vers_win=""; vers_linux=""; vers_osx=""; unclassified=""
  while IFS= read -r f; do
    if classify "${f}"; then
      case "${CH}" in
        win)   vers_win="${vers_win}${VER}${NL}" ;;
        linux) vers_linux="${vers_linux}${VER}${NL}" ;;
        osx)   vers_osx="${vers_osx}${VER}${NL}" ;;
      esac
    else
      unclassified="${unclassified}${f}${NL}"
    fi
  done <<LISTING
${listing}
LISTING

  newest() { printf '%s' "$1" | sed '/^$/d' | sort -u | sort -t. -k1,1n -k2,2n -k3,3n -k4,4n | tail -n "${KEEP_N}"; }
  keep_win=$(newest "${vers_win}")
  keep_linux=$(newest "${vers_linux}")
  keep_osx=$(newest "${vers_osx}")

  keep_feed=""; keep_recent=""; to_delete=""
  while IFS= read -r f; do
    if in_list "${f}" "${feed}"; then
      keep_feed="${keep_feed}${f}${NL}"
    elif ! classify "${f}"; then
      :
    else
      case "${CH}" in
        win)   keepset="${keep_win}" ;;
        linux) keepset="${keep_linux}" ;;
        osx)   keepset="${keep_osx}" ;;
      esac
      if in_list "${VER}" "${keepset}"; then
        keep_recent="${keep_recent}${f}${NL}"
      else
        to_delete="${to_delete}${f}${NL}"
      fi
    fi
  done <<LISTING
${listing}
LISTING

  count() { printf '%s' "$1" | sed '/^$/d' | wc -l | tr -d ' '; }
  echo
  echo "-- KEEP, referenced by a live feed manifest ($(count "${keep_feed}")):"
  printf '%s' "${keep_feed}" | sed 's/^/   /'
  echo "-- KEEP, within the ${KEEP_N} newest versions of its channel ($(count "${keep_recent}")):"
  printf '%s' "${keep_recent}" | sed 's/^/   /'
  echo "   win:   $(printf '%s' "${keep_win}" | tr '\n' ' ')"
  echo "   linux: $(printf '%s' "${keep_linux}" | tr '\n' ' ')"
  echo "   osx:   $(printf '%s' "${keep_osx}" | tr '\n' ' ')"
  echo "-- KEEP, anything not classifiable as a package ($(count "${unclassified}")):"
  printf '%s' "${unclassified}" | sed 's/^/   /'
  echo "-- DELETE, unreferenced packages older than the window ($(count "${to_delete}")):"
  printf '%s' "${to_delete}" | sed 's/^/   /'
  echo
  if [ -z "${to_delete}" ]; then
    echo "nothing to prune in the bucket."
    return 0
  fi
  if [ "${R2_PRUNE_DRY_RUN:-0}" = "1" ]; then
    echo "dry run: nothing deleted from the bucket."
    return 0
  fi

  deleted=0
  failed=0
  while IFS= read -r f; do
    case "${f}" in
      "") continue ;;
      .*|*/*)  echo "::warning::refusing to delete '${f}' (not a plain package name)"; continue ;;
      *.nupkg) ;;
      *)       echo "::warning::refusing to delete '${f}' (not a .nupkg)"; continue ;;
    esac
    if r2 s3 rm "s3://${R2_BUCKET}/${RELEASES_PREFIX}${f}" --only-show-errors </dev/null; then
      deleted=$((deleted + 1))
    else
      failed=$((failed + 1))
      echo "::warning::could not delete ${RELEASES_PREFIX}${f} from the bucket"
    fi
  done <<DELETES
${to_delete}
DELETES
  echo "pruned ${deleted} package(s) from the bucket, ${failed} failed."
}

case "${1:-}" in
  configured) cmd_configured ;;
  put)        shift; cmd_put "$@" ;;
  prune)      cmd_prune ;;
  *)          echo "usage: r2.sh configured | put <file> <key> <cache-control> [content-disposition] | prune" >&2; exit 64 ;;
esac
