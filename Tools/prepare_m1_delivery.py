"""Copy only allow-listed, portable M1 evidence; exclude runtimes and private data.

Run from any working directory. Original local acceptance evidence is untouched.
The manifest records original/copied hashes and JSONL/log filename conversions.
"""
from pathlib import Path
import hashlib
import json
import os
import shutil
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Docs/Acceptance/M1/20261020-xLua"
OUTPUT = ROOT / "Docs/Delivery/M1"
OUTPUT.mkdir(parents=True, exist_ok=True)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


matrix = load(SOURCE / "StageE/AcceptanceMatrix.json")
assert len(matrix["steps"]) == 58 and all(x["status"] == "pass" for x in matrix["steps"])
records = {}


def export(relative):
    source = (ROOT / relative).resolve()
    assert source.is_file() and source.is_relative_to(ROOT), relative
    if not source.is_relative_to(SOURCE):
        return source.relative_to(ROOT).as_posix()  # Existing project source: link directly.
    assert source.suffix.lower() in {".json", ".jsonl", ".md", ".xml", ".trx", ".txt", ".log", ".report", ".ps1", ".py", ".cs", ".csproj", ".stdout", ".stderr"}, relative
    assert not any(x in source.parts for x in ("tokens", "commands", "acks", "yoo", "cdn")), relative
    destination = OUTPUT / "Evidence" / source.relative_to(SOURCE)
    conversion = None
    if source.suffix == ".jsonl":
        destination = destination.with_suffix(".json")
        conversion = "JSONL records exported as a JSON array"
    elif source.suffix == ".log":
        destination = destination.with_suffix(".txt")
        conversion = "Log filename changed to .txt; content unchanged"
    elif source.suffix in (".stdout", ".stderr"):
        destination = destination.with_name(destination.name + ".txt")
        conversion = "Build output filename extended with .txt; content unchanged"
    destination.parent.mkdir(parents=True, exist_ok=True)
    if conversion and source.suffix == ".jsonl":
        data = [json.loads(x) for x in source.read_text(encoding="utf-8-sig").splitlines() if x.strip()]
        destination.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    else:
        shutil.copyfile(source, destination)
    result = destination.relative_to(ROOT).as_posix()
    records[relative] = {"source": relative, "sourceSha256": digest(source), "export": result,
                         "exportSha256": digest(destination), "bytes": destination.stat().st_size,
                         "conversion": conversion}
    return result


# PNGs are separately allow-listed because they are actual inspected Unity frames.
def export_picture(relative):
    source = (ROOT / relative).resolve()
    assert source.is_relative_to(SOURCE) and source.suffix == ".png"
    destination = OUTPUT / "Evidence" / source.relative_to(SOURCE)
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, destination)
    result = destination.relative_to(ROOT).as_posix()
    records[relative] = {"source": relative, "sourceSha256": digest(source), "export": result,
                         "exportSha256": digest(destination), "bytes": destination.stat().st_size, "conversion": None}
    return result


for step in matrix["steps"]:
    original = list(step["evidence"])
    step["localEvidence"] = original
    step["evidence"] = [export_picture(x) if x.endswith(".png") else export(x) for x in original]

extras = ["Root-Finalization/final-audit.json", "Root-Finalization/closing-check.json",
          "Root-Finalization/Final-Consistency-Review-result.md", "Root-Finalization/ConnectionIsolation-Review-result.md",
          "Root-Finalization/PlayMode-Final/command.txt", "Root-Finalization/Foundation-Final/command.txt",
          "Root-Finalization/Input-Fixed/command.txt", "Root-Finalization/Input-Final/results.xml",
          "Root-Finalization/Expiry-Red/results.xml", "Root Final 100 R2/baseline-fingerprint.json",
          "Root-Finalization/fixture-r2-build.stdout", "Root-Finalization/fixture-r2-build.stderr"]
# Stdout/stderr carry only build diagnostics; export as .txt to keep the allow list strict.
extras = [x for x in extras if not x.endswith((".stdout", ".stderr"))]
for name in ("Build-FinalCandidate.ps1", "NativeHarness.ps1", "Run-NativeAccountsAndPatch.ps1", "Run-NativeBadResource.ps1",
             "Run-NativeRecovery.ps1", "Run-NativeProtocolMismatch.ps1", "Run-FinalAotNegative.ps1",
             "Run-FinalFoundation.ps1", "db_fixture.py", "ProtocolFixture/Program.cs", "ProtocolFixture/ProtocolFixture.csproj",
             "Finalize-Acceptance.py"):
    extras.append("Root-Finalization/" + name)
for name in extras:
    export((SOURCE / name).relative_to(ROOT).as_posix())

matrix["source"] = "Assets/ReferenceDoc/06_M1_基础框架任务拆解.md"
matrix["sourceSha256AtPackaging"] = digest(ROOT / matrix["source"])
matrix["audit"] = export((SOURCE / "Root-Finalization/final-audit.json").relative_to(ROOT).as_posix())
matrix["packagingNote"] = "Evidence references point to portable copies; localEvidence preserves original relative paths. Build installations/caches/private inputs are not source artifacts."
(OUTPUT / "AcceptanceMatrix.json").write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

# Verify actual known private fixture values are absent from every exported payload.
secrets = set()
for run in ("Native-20261021-141638-780", "Recovery-20261021-141811-730", "BadResource-20261021-141811-409", "Protocol-20261021-141823-622"):
    location = SOURCE / "Root-Finalization" / run
    for path in location.rglob("creds.private"):
        lines = path.read_text(encoding="utf-8-sig").splitlines()
        if len(lines) > 1:
            secrets.add(lines[1].encode())
    for path in list(location.rglob("m1-token.txt")) + list(location.rglob("old-token.private")):
        value = path.read_text(encoding="utf-8-sig").strip()
        if value:
            secrets.add(value.encode())
for record in records.values():
    payload = (ROOT / record["export"]).read_bytes()
    assert not any(secret in payload for secret in secrets), "Private fixture value found in exported evidence"


def link(relative, label=None):
    path = ROOT / relative
    assert path.exists(), relative
    target = quote(Path(os.path.relpath(path, OUTPUT)).as_posix(), safe="/._-")
    return f"[{label or path.name}]({target})"


audit = load(SOURCE / "Root-Finalization/final-audit.json")
lines = ["# M1 验收与源码交付", "", "验收日期：2026-10-21；范围：Windows x64标准IL2CPP/xLua，.NET 8服务端。", "",
         "**T01–T68完成，58项验收通过。** 本文件与包含它的源码提交一起交付；M2仅制定计划，尚未实施。", "",
         "## 1. 版本与结果", "", "- App0.3.0 / Proto1 / bridge m1-bridge-0.3；Res/Config100→101。",
         "- 实际Native账号/更新、坏资源真Retry、完整退避、同库强杀恢复、Token过期、双客户端顶号、旧Token及106均通过。",
         "- 同安装同缓存下载42→5→0；三个安装文件SHA不变。",
         "- 当前服务端225/225、受影响PlayMode9/9、基础EditMode69/69、输入模态PlayMode7/7；失败跳过均0。",
         "- C1的27项与C2的13项复用证据单独标注，不重复计入当前测试总数。", "",
         "| 安装文件 | SHA-256 |", "|---|---|"]
for key, value in audit["installedSha256"].items():
    lines.append(f"| {key} | `{value.lower()}` |")
lines += ["", "## 2. 可移植证据", "", "- " + link("Docs/Delivery/M1/AcceptanceMatrix.json", "58项机器可读矩阵"),
          "- " + link("Assets/ReferenceDoc/06_M1_基础框架任务拆解.md", "实现方法、问题及方案回填"),
          "- " + link("Assets/ReferenceDoc/Logs/M1_07_Logs.md", "具体命令、函数参数、完整脚本与验证过程"),
          "- " + link("Assets/ReferenceDoc/06_M2_开发计划.md", "M2设计和80项待办计划"), "",
          "| 编号 | 实际结果 | 随源码提供的证据 |", "|---|---|---|"]
for step in matrix["steps"]:
    lines.append(f"| {step['id']} | {step['actual']} | {'；'.join(link(x) for x in step['evidence'])} |")
picture = export_picture((SOURCE / "Root-Finalization/Native-20261021-141638-780/client/shots/101_patch_window.png").relative_to(ROOT).as_posix())
lines += ["", "## 3. 实际热更新画面", "", "!" + link(picture, "同一安装更新101后的真实新面板"), "",
          "## 4. 上传范围与复现", "",
          "仓库根已从Assets上移到完整Unity工程根，原提交历史保留。Assets/Packages/ProjectSettings/Server/Shared/Tools均纳入版本控制；源码中的Unity.meta及必需插件/导表DLL保留。",
          "Library、Temp、安装包、CDN副本、数据库、Token、私有凭证、原始Cursor流和大体积本地验收工作目录由.gitignore排除。它们不是本次源码交付的一部分。",
          "本目录仅复制矩阵明确引用的报告/状态/源码快照/实际截图。JSONL转为JSON数组，日志文件改.txt后缀；manifest.json记录原件与导出件SHA及转换，原本地证据不修改。",
          "用工程根Tools脚本重建正式客户端/服务端。Evidence中的Root-Finalization脚本是原验收快照，若重新运行需要恢复其原相对目录、显式RepoRoot及生成对应本地候选；不能把已有安装SHA当作新机器构建的固定输出保证。",
          "当前可上传包未携带Unity安装、MSVC/IL2CPP工具链、protoc或本机凭证。工具定位与失败退出见根README和原脚本参数。", ""]
(OUTPUT / "M1_Acceptance.md").write_text("\n".join(lines), encoding="utf-8")
manifest = {"date": "2026-10-21", "acceptancePassed": 58, "privateFixtureMatches": 0,
            "knownPrivateValuesChecked": len(secrets), "files": sorted(records.values(), key=lambda x: x["export"])}
(OUTPUT / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"copiedEvidenceFiles": len(records), "bytes": sum(x["bytes"] for x in records.values()),
                  "privateFixtureMatches": 0, "acceptancePassed": 58}))
