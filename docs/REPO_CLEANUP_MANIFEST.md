# 仓库工程卫生清理清单（已执行并核验）

> 生成日期：2026-11-02 ｜ 数据来源：PowerShell 实测（`Get-ChildItem -Force`）
> **状态：✅ 已执行"彻底删除"并核验通过（2026-11-02）。**
> 核验结果：根目录 `_*` 文件残留 0；`_dup_backup/`、`_repair/` 已删除；
> `README.md` / `ARCHITECTURE.md` / `YamaEngine/` / `.workbuddy/` / `YamaEditor.sln` 均完好。
> 共清理 266 项，释放约 275 MB。

---

## 1. 总量

| 项 | 数量 | 占用 |
|---|---|---|
| 根目录 `_*` 文件 | 83 个 | 约 223 MB |
| `_dup_backup/`（`.bak`） | 157 个 | 约 6.9 MB |
| `_repair/`（`.txt`） | 26 个 | 约 45.5 MB |
| **合计** | **266 项** | **约 275 MB** |

**全部已被 `.gitignore` 忽略（未入库）**：见 `.gitignore:81`（`_dup_backup/`）、`:99-104`（`/_*.py|txt|csv|json|md`、`/_repair/`）、`:44,83`（`*.log`）。
→ 删除不影响 git 历史；均为历史编译修复/分析的可再生产物。

---

## 2. 分类说明

| 类别 | 示例 | 用途（不再需要） |
|---|---|---|
| 构建日志 | `_m1.log`…`_m22.log`、`_ba.log`、`_build*.log`、`_chk*.log`、`_final*.log`、`_rc*.log`（多为 6–7 MB） | 历史 `dotnet build` 输出 |
| 错误分析文本 | `_err*.txt`、`_errors_uniq.txt`、`_all_errors.txt`、`_missing_by_file.txt`、`_tests_errors.txt` | CS 错误归类 |
| 修复脚本 | `_fix*.py`、`_inject.py`、`_analyze.py`、`_classify*.py`、`_clean_inject.py`、`_eval_*.py` | 一次性批量修复，已完成使命 |
| 数据文件 | `_inject_plan.json`、`_exclude_members.json`、`_inventory_out.txt` | 修复中间产物 |
| `_dup_backup/` | 157 个 `.bak` | 修复前备份，源码已定稿 |
| `_repair/` | 26 个 `.txt` | 修复过程记录 |

---

## 3. 待清理明细（根目录 `_*`，83 个）

```
_a.log  _all_errors.txt  _analyze.py  _b.log  _ba.log  _bb.log
_build2.log  _build3.log  _build4.log  _build5.log  _build6.log
_build7.log  _build8.log  _build9.log  _build_full.log
_chk1.log  _chk2.log  _chk3.log  _classify.py  _classify2.py  _classify_log.txt
_clean_inject.py  _cls_err.txt  _cs1061_members.txt  _cs1061_types.txt
_err2.txt  _errcodes.txt  _errfiles.txt  _errfiles2.txt  _errmsg.txt
_errmsg_dedup.txt  _errors_uniq.txt  _err_mem.txt  _err_now.txt
_eval_errdist.py  _eval_errlist.py  _eval_errors_grouped.txt  _exclude_members.json
_final1.log  _final2.log  _fix3.py  _fix_all.py  _fix_bt.py  _fix_ecs_iterator.py
_fix_vulkan_alias.py  _inject.py  _inject_plan.json  _inventory.py  _inventory_out.txt
_m1.log _m2.log _m3.log _m4.log _m5.log _m6.log _m7.log _m8.log _m9.log
_m10.log _m11.log _m12.log _m13.log _m14.log _m15.log _m16.log _m17.log _m18.log
_m19.log _m20.log _m21.log _m22.log
_missing_by_file.txt  _rc1.log _rc2.log _rc3.log _rc4.log _rc5.log
_restore.log  _t.txt  _cur_build.log  _rebuild_now.log  _tests_errors.txt
_verify_build.log
```

目录：`_dup_backup/`（157 `.bak`）、`_repair/`（26 `.txt`）

---

## 4. 建议执行方式（确认后）

**优先移入回收站（可恢复）**，PowerShell：

```powershell
# 移入回收站（推荐，可恢复）
Add-Type -AssemblyName Microsoft.VisualBasic
Get-ChildItem -Path 'G:\Yama-Engine' -Force -File | Where-Object { $_.Name -like '_*' } |
  ForEach-Object { [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($_.FullName,'OnlyErrorDialogs','SendToRecycleBin') }
[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory('G:\Yama-Engine\_dup_backup','OnlyErrorDialogs','SendToRecycleBin')
[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory('G:\Yama-Engine\_repair','OnlyErrorDialogs','SendToRecycleBin')
```

彻底删除（不可恢复，释放约 275 MB）：
```powershell
Get-ChildItem -Path 'G:\Yama-Engine' -Force -File | Where-Object { $_.Name -like '_*' } | Remove-Item -Force
Remove-Item -Path 'G:\Yama-Engine\_dup_backup','G:\Yama-Engine\_repair' -Recurse -Force
```

> ⚠️ 切勿删除 `.workbuddy/`（项目数据）与任何源码/文档。以上仅针对 `_*` 前缀历史产物与两个临时目录。
