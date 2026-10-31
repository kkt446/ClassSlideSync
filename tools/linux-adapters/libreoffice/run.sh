#!/usr/bin/env bash
# 一行启动：用系统 Python（pyuno 装在系统 site-packages 里）跑适配器。
exec /usr/bin/python3 "$(dirname "$0")/slidesync_lo.py" "$@"
