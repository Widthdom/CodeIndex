#!/bin/sh
set -eu

if [ "$(id -u)" -eq 0 ]; then
    target_uid="${CDIDX_RUN_UID:-}"
    target_gid="${CDIDX_RUN_GID:-}"

    if [ -z "$target_uid" ] && [ -e /repo ]; then
        target_uid="$(stat -c '%u' /repo)"
    fi
    if [ -z "$target_gid" ] && [ -e /repo ]; then
        target_gid="$(stat -c '%g' /repo)"
    fi

    target_uid="${target_uid:-10001}"
    target_gid="${target_gid:-10001}"

    if [ "$target_uid" != "0" ]; then
        export HOME=/repo
        exec su-exec "${target_uid}:${target_gid}" cdidx "$@"
    fi
fi

exec cdidx "$@"
