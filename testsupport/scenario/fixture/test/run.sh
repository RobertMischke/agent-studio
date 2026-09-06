#!/bin/sh
set -eu
test "$(cat app/value.txt)" = "$(cat test/expected.txt)"
