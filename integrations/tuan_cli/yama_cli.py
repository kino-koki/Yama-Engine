#!/usr/bin/env python3
# 独立 CLI 入口：Tuan-CLI 无需安装本插件即可 subprocess 调用。
#   python integrations/tuan_cli/yama_cli.py capabilities
#   python integrations/tuan_cli/yama_cli.py codeoss-minimal
#   python integrations/tuan_cli/yama_cli.py invoke --action sota_status
from __future__ import annotations
import json, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import subcommand

def main():
    print(json.dumps(subcommand.handle(sys.argv[1:]), ensure_ascii=False))
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
