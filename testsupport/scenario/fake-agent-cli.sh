#!/bin/sh
set -eu

case "${1:-}" in
  --version)
    printf 'codex-cli scenario-fixed-1.0.0\n'
    ;;
  login)
    printf 'Logged in using scenario credentials\n'
    ;;
  *)
    printf '{"type":"agent_message","text":"fixed compose fake CLI output"}\n'
    printf '[[TASK_DONE]]\n'
    ;;
esac
