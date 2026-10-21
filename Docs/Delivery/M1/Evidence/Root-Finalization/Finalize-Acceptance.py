"""Reconcile the 58 existing requirements with actual reports and final R2 artifacts.

No test is executed here. Assertions check recorded results, business state, source
fingerprints and credentials in owned logs before writing the acceptance record.
"""
from pathlib import Path
import collections
import datetime
import hashlib
import json
import sqlite3
import xml.etree.ElementTree as ET

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[4]
EVIDENCE = HERE.parent
NATIVE = HERE / "Native-20261021-141638-780"
RECOVERY = HERE / "Recovery-20261021-141811-730"
BAD = HERE / "BadResource-20261021-141811-409"
PROTOCOL = HERE / "Protocol-20261021-141823-622"
NEGATIVE = HERE / "AotNegative-R2-20261021-141623-394"
BASE = EVIDENCE / "Root Final 100 R2"
PUBLISH = EVIDENCE / "Root Final Publish R2"
SERVER = HERE / "ServerTests/root-server-final225.trx"
C1 = EVIDENCE / "StageC1/Root-NarrowFix/Run-20261020-172926/playmode-results.xml"
C2 = EVIDENCE / "StageC2/Root-NarrowFix/Run-20261020-220350/playmode-results.xml"
PLAY = HERE / "PlayMode-Final/results.xml"
FOUNDATION = HERE / "Foundation-Final/results.xml"
INPUT = HERE / "Input-Fixed/results.xml"


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.file_digest(Path(path).open("rb"), "sha256").hexdigest()


def passed(path):
    result = read(path)
    assert result["passed"] is True, str(path)
    return result


def unity_report(path, count):
    result = ET.parse(path).getroot()
    assert result.get("result") == "Passed", str(path)
    assert int(result.get("total")) == count, str(path)
    assert int(result.get("failed")) == int(result.get("skipped")) == 0
    cases = result.findall(".//test-case")
    assert len(cases) == count and all(x.get("result") == "Passed" for x in cases)
    return cases


def model(state):
    return json.loads(state["luaModel"])


def require(state, **expected):
    for key, value in expected.items():
        assert state[key] == value, (key, state.get(key), value)


# Read actual results, never infer pass from process exit alone.
native = passed(NATIVE / "result.json")
recovery = passed(RECOVERY / "result.json")
bad = passed(BAD / "result.json")
protocol = passed(PROTOCOL / "result.json")
negative = passed(NEGATIVE / "result.json")
build = passed(HERE / "r2-build-result.json")
cli = passed(HERE / "CliNegative/result.json")
server_config = passed(HERE / "server-config101-read.json")
for name in ("r2-client-build", "r2-config101", "r2-patch-build", "r2-restore-config100"):
    assert (HERE / (name + ".exit.txt")).read_text().strip() == "0"
unity_report(C1, 27)
unity_report(C2, 13)
unity_report(PLAY, 9)
foundation_cases = unity_report(FOUNDATION, 69)
unity_report(INPUT, 7)
trx = ET.parse(SERVER).getroot()
counters = trx.find(".//{*}Counters")
assert counters.get("total") == counters.get("passed") == "225"
assert counters.get("failed") == counters.get("notExecuted") == "0"
server_cases = trx.findall(".//{*}UnitTestResult")
assert len(server_cases) == 225 and all(x.get("outcome") == "Passed" for x in server_cases)
server_names = {x.get("testName") for x in server_cases}
for name in ("OnePeerAbruptExit_KeepsOtherAuthenticatedPeerAndListenersAlive",
             "AuthenticatedPeerExit_KeepsHostListeningAndAcceptsNextLogin",
             "DatabaseReadOnly_KeepsDirty_ThenWritableRetryPersists"):
    assert name in server_names

initial = read(NATIVE / "A02-B02-login100.json")
registered = read(NATIVE / "A03-register-not-authorized.json")
s1 = read(NATIVE / "A04-S1-home100.json")
logout = read(NATIVE / "A06-logout.json")
require(initial, resVersion="100", patchModule="False", winPatch="False", globalPatch="", winLogin="True")
require(registered, auth="False", hasToken="False", registerMessage="注册成功，请登录")
require(logout, auth="False", hasToken="False", winLogin="True", winHome="False")
assert model(logout)["info"]["playerId"] == "0"
for name in ("A07-login-again", "A10-reopen-same-install", "B07-home101", "B10-cached-home"):
    assert model(read(NATIVE / (name + ".json"))) == model(s1)
updated = read(NATIVE / "B08-real-prefab-and-global.json")
require(updated, resVersion="101", winPatch="True", patchModule="True", globalPatch="热更新验证成功")
assert [int(native[k]) for k in ("initialDownloadFiles", "updateDownloadFiles", "cachedDownloadFiles")] == [42, 5, 0]
before, after = read(NATIVE / "install-before.json"), read(NATIVE / "install-after.json")
assert before == after
installed = {"exe": BASE / "Player/Farming.exe", "gameAssembly": BASE / "Player/GameAssembly.dll",
             "metadata": BASE / "Player/Farming_Data/il2cpp_data/Metadata/global-metadata.dat"}
assert all(sha(path) == before[key].lower() for key, path in installed.items())

# Verify the complete frozen source set is still exactly the recorded bytes.
fingerprint = read(BASE / "baseline-fingerprint.json")
assert fingerprint["schema"] == "m1-baseline-fingerprint-v2"
files = fingerprint["files"]
assert len(files) == fingerprint["count"] == len({x["path"] for x in files}) == 3342
assert all(sha(ROOT / x["path"]) == x["sha256"].lower() for x in files)
assert "Assets/Framework/Lua/XLua/Plugins/x86_64/xlua.dll" in {x["path"] for x in files}
assert "Packages/packages-lock.json" in {x["path"] for x in files}
for version, directory in ((100, BASE / "Publish/100"), (101, PUBLISH / "101")):
    report = (directory / f"DefaultPackage_{version}.report").read_text(encoding="utf-8-sig")
    assert ("PatchDemoWindow" in report) == (version == 101)
    for bundle in directory.glob("*.bundle"):
        assert bundle.stat().st_size > 8

# Read-only SQL confirmation; report only counts and public business data.
with sqlite3.connect((NATIVE / "players.db").as_uri() + "?mode=ro", uri=True) as db:
    db_counts = {table: db.execute("SELECT COUNT(*) FROM " + table).fetchone()[0]
                 for table in ("account", "player")}
assert db_counts == {"account": 1, "player": 1}
database = read(NATIVE / "A05-A08-database-snapshot.json")["players"][0]
info = model(s1)["info"]
for sql_key, lua_key in (("id", "playerId"), ("nickname", "nickname"), ("level", "level"),
                         ("exp", "exp"), ("gold", "gold"), ("gem", "gem"), ("ticket", "ticket"),
                         ("shard", "shard"), ("bag_slots", "bagSlots"), ("guide_step", "guideStep")):
    assert str(database[sql_key]) == str(info[lua_key])
assert recovery["committedRevision"] == 1 and recovery["fullSnapshotAndTokenPreserved"]
assert recovery["nativeBackoff"][:6] == [1, 2, 4, 8, 16, 30]
r1 = read(RECOVERY / "C01-R1.json")
for name in ("C04-restored", "C05-R2", "C07-restored-R2", "C10-relogin"):
    assert model(read(RECOVERY / (name + ".json"))) == model(r1)
assert model(read(RECOVERY / "second-client/C11-B-current.json")) == model(r1)
expired = read(RECOVERY / "C09-expired-return-login.json")
require(expired, lastBiz="204", auth="False", hasToken="False", winLogin="True", reconnectRunning="False")
assert model(expired)["info"]["playerId"] == "0"
require(read(RECOVERY / "C11-A-kicked.json"), auth="False", hasToken="False", winLogin="True", reconnectRunning="False")
assert read(RECOVERY / "C12-old-token-rejection.json")["hasSnapshot"] is False
assert protocol["responseCode"] == 106 and protocol["reconnectRunning"] is False
error = read(BAD / "B12-visible-error-no-activation.json")
require(error, entry="False", committed="False", winLogin="False")
assert bad["attempts"] == ["1", "2"] and bad["installationUnchanged"]
assert negative["playerUnchanged"] and negative["noRejectedPointer"] and negative["publishedPointerUnchanged"]
duplicate = EVIDENCE / "StageE/Root-ProtoDuplicate/Run-20261020-223154-240/result.json"
dup = passed(duplicate)
assert sha(ROOT / "Tools/gen_proto.ps1").upper() == dup["sourceScriptSha256"].upper()
assert dup["lastSuccessfulGeneratedUnchanged"]

# Both current export copies must match byte-for-byte, and actual 101 readers agree.
def data_hashes(directory):
    return {p.name: sha(p) for p in directory.glob("*.json")}

config_hashes = data_hashes(ROOT / "Server/ConfigLuban")
assert config_hashes and config_hashes == data_hashes(ROOT / "Assets/ConfigLuban")
assert config_hashes == data_hashes(ROOT / "Assets/StreamingAssets/ConfigLuban")
assert server_config["version"] == 101 and server_config["value"] == updated["globalPatch"]

# Check owned logs against actual known private inputs, without revealing those inputs.
secrets = set()
logs = []
for run in (NATIVE, RECOVERY, BAD, PROTOCOL):
    for private in run.rglob("creds.private"):
        lines = private.read_text(encoding="utf-8-sig").splitlines()
        if len(lines) >= 2:
            secrets.add(lines[1])
    for token in list(run.rglob("m1-token.txt")) + list(run.rglob("old-token.private")):
        value = token.read_text(encoding="utf-8-sig").strip()
        if value:
            secrets.add(value)
    logs.extend(p for p in run.rglob("*") if p.is_file() and p.suffix in (".log", ".stdout", ".stderr"))
for log in logs:
    text = log.read_text(encoding="utf-8-sig", errors="replace")
    assert not any(secret in text for secret in secrets), "Private value found in " + str(log)
client_log = NATIVE / "client/client-logs/client.log"
assert client_log.stat().st_size > 0
metric_log = NATIVE / "server-100-account/server.log"
assert "metrics connections=" in metric_log.read_text(encoding="utf-8-sig")

audit = {"passed": True, "date": "2026-10-21", "frozenFiles": 3342, "installedSha256": before,
         "serverTests": 225, "affectedPlayMode": 9, "foundationEditMode": 69, "inputPlayMode": 7,
         "reusedC1PlayMode": 27, "reusedC2PlayMode": 13, "databaseCounts": db_counts,
         "knownPrivateValuesChecked": len(secrets), "ownedLogsChecked": len(logs), "privateValueMatches": 0,
         "matchingCurrentConfigJsonCount": len(config_hashes), "config101Value": server_config["value"]}
(HERE / "final-audit.json").write_text(json.dumps(audit, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

mapping = {}


def item(key, actual, *evidence):
    assert key not in mapping
    for path in evidence:
        assert Path(path).exists(), str(path)
    mapping[key] = (actual, [Path(x).relative_to(ROOT).as_posix() for x in evidence])


actions = NATIVE / "actions.jsonl"
item("A01", "新隔离DB初始化schema2；正式TCP/HTTP就绪；最终停机cleanupFailures=0。", actions, metric_log)
item("A02", "最终R2标准IL2CPP100进入真实Login，entry/committed=True。", NATIVE / "A02-B02-login100.json", NATIVE / "client/shots/100_login.png")
item("A03", "真实按钮注册成功；仍未认证且无Token；独立DB account/player各1行。", NATIVE / "A03-register-not-authorized.json", HERE / "final-audit.json")
item("A04", "真实Lua登录/模型应用/Token提交/家园HUD一致：ID1、FinalNick、Lv1。", NATIVE / "A04-S1-home100.json", NATIVE / "client/shots/100_home.png")
item("A05", "S1完整Lua业务模型已保存；公开业务字段逐项与只读DB快照一致。", NATIVE / "A04-S1-home100.json", NATIVE / "A05-A08-database-snapshot.json")
item("A06", "真实LogoutReq1009被处理；返回Login、无Token、模型playerId0；服务端登出撤销/保存测试通过。", NATIVE / "A06-logout.json", metric_log, SERVER)
item("A07", "再次登录完整业务模型等于S1。", NATIVE / "A07-login-again.json")
item("A08", "命令回执后正常退出；拥有进程exit0、日志保留可读。", actions)
item("A09", "同安装同缓存的新进程重新进入Login后再登录。", actions, NATIVE / "A10-reopen-same-install.json")
item("A10", "重开后登录的完整Lua业务模型与S1相等。", NATIVE / "A10-reopen-same-install.json", HERE / "final-audit.json")
item("A11", "PlayerPersistenceTests实际修改后保存重载、只读失败恢复均通过；Native恢复另验证非默认gold201/bag1×7/revision1。", SERVER, RECOVERY / "prepared-saved-state.json")
item("B01", "最终R2安装三个SHA实际计算，标准Win64 IL2CPP/App0.3/Proto1。", NATIVE / "install-before.json", BASE / "baseline-manifest.json")
item("B02", "100无新模块/Global/窗口；实际100 Yoo报告无PatchDemoWindow。", NATIVE / "A02-B02-login100.json", BASE / "Publish/100/DefaultPackage_100.report")
item("B03", "100进程完全退出，后续101启动为新进程/新Lua环境。", actions)
item("B04", "正式补丁脚本exit0；101新增Lua/Prefab/Global；复用R2安装。", HERE / "r2-patch-build.exit.txt", PUBLISH / "101/DefaultPackage_101.report", NATIVE / "B08-real-prefab-and-global.json")
item("B05", "完整101目录先复制到自有CDN，所有flat文件复制完成后才切指针。", HERE / "Run-NativeAccountsAndPatch.ps1", actions)
item("B06", "原子换version.json为101；100目录保留；ServerConfig101加载成功。", actions, NATIVE / "B06-updated101.json")
item("B07", "原路径/同缓存启动101，实际差异下载5文件，少于100初装42文件。", NATIVE / "B06-updated101.json", NATIVE / "result.json")
item("B08", "真实UiBridge异步打开新Prefab；实际Global为热更新验证成功；Root已查看完整帧。", NATIVE / "B08-real-prefab-and-global.json", NATIVE / "client/shots/101_patch_window.png")
item("B09", "exe/GameAssembly/global-metadata更新前后逐一相同。", NATIVE / "install-before.json", NATIVE / "install-after.json")
item("B10", "同缓存再次启动101下载0文件，登录后新Prefab仍可打开。", NATIVE / "B10-cached101.json", NATIVE / "B10-cached-home.json", actions)
item("B11", "仅截断自有CDN的实际bundle副本，新缓存隔离；原清单保留。", BAD / "result.json", HERE / "Run-NativeBadResource.ps1")
item("B12", "实际可见更新失败/重试；Lua未启动、候选未提交。", BAD / "B12-visible-error-no-activation.json", BAD / "client/shots/download_error.png")
item("B13", "恢复原bundle字节并核对原SHA256。", BAD / "result.json", BAD / "actions.jsonl")
item("B14", "点击生产Retry按钮后进入Login；仅prepare尝试1、2，无双流水线。", BAD / "B14-retry-success.json", BAD / "result.json", BAD / "client/shots/retry_login.png")
item("C01", "production PlayerStore.Save/reopen产生revision1非默认快照，Native读取gold201/bag1×7作为R1。", RECOVERY / "prepared-saved-state.json", RECOVERY / "C01-R1.json", RECOVERY / "C01-R1-database.json")
item("C02", "关闭自有测试通路后真实遮罩出现；全屏raycastTarget按源码阻止输入，Root已查看实际遮罩。", RECOVERY / "C02-mask.json", RECOVERY / "client/shots/reconnect_mask.png", ROOT / "Assets/Framework/Account/DisconnectOverlay.cs")
item("C03", "实际观察75秒以上，退避1/2/4/8/16/30，后续30；单循环尝试序列。", RECOVERY / "C03-real-backoff.json", RECOVERY / "actions.jsonl")
item("C04", "原端口/原DB恢复，无密码命令，原Token与完整Lua模型保持，Home恢复且遮罩消失。", RECOVERY / "C04-restored.json", RECOVERY / "result.json", RECOVERY / "client/shots/restored_home.png")
item("C05", "强杀前只读DB确认SavedRevision1与实际snapshot摘要。", RECOVERY / "C05-R2.json", RECOVERY / "C05-R2-database.json")
item("C06", "核对exe/父PID/精确DB后仅强杀自有dotnet，客户端进入重连。", RECOVERY / "actions.jsonl", HERE / "Run-NativeRecovery.ps1")
item("C07", "同DB/配置/端口重启，原Token自动恢复，完整业务模型等于R2。", RECOVERY / "C07-restored-R2.json", RECOVERY / "result.json")
item("C08", "隔离DB真实事务将1条当前Token到期时间设为0；报告不含Token。", RECOVERY / "C08-expired-records.json")
item("C09", "真实204已返回Login，Token/模型清空，重连停止；已复验本轮修复。", RECOVERY / "C09-expired-return-login.json", PLAY)
item("C10", "通过真实Login按钮重新获得认证，完整快照保持R2。", RECOVERY / "C10-relogin.json")
item("C11", "第二个Native同账号登录，A清Token/模型回Login且不抢回，B保持Home。", RECOVERY / "C11-A-kicked.json", RECOVERY / "second-client/C11-B-current.json", RECOVERY / "client/shots/kicked_login.png")
item("C12", "用A旧Token实际TCP请求返回204且无快照；B仍认证，未被抢回。", RECOVERY / "C12-old-token-rejection.json", RECOVERY / "actions.jsonl")
item("C13", "实际Proto1客户端对Proto2服务端Login返回106，UI显示版本不兼容请更新且无重连。", PROTOCOL / "result.json", PROTOCOL / "C13-version-mismatch.json", PROTOCOL / "client/shots/version_update_required.png")
item("D01", "带空格Root Final 100 R2目录完整gen_proto/gen_config/GenAll/Yoo/IL2CPP构建exit0。", HERE / "r2-client-build.exit.txt", HERE / "r2-client-build.stdout", BASE / "baseline-manifest.json")
item("D02", "正式build_patch基于R2完整指纹exit0输出独立101；安装三SHA保持。", HERE / "r2-patch-build.exit.txt", HERE / "r2-patch-build.stdout", NATIVE / "install-after.json")
item("D03", "正式run_server指定独立DB/配置/端口，真实TCP/HTTP/metrics可用，正常停机cleanupFailures0。", actions, metric_log, SERVER)
item("D04", "dotnet test实际225/225，失败0、跳过0；新增并存连接RST隔离测试通过。", SERVER, HERE / "ServerTests/r2-exit.txt")
item("D05", "当前PlayMode的UIResource两个实际用例均通过，开关和场景取消后资源回基线。", PLAY)
item("D06", "不存在Unity路径实际exit1，明确缺失路径，未创建成功输出目录。", HERE / "CliNegative/result.json", HERE / "CliNegative/stderr.txt")
item("D07", "真实protoc生成后重复ID校验exit1；最后成功的5个生成文件SHA不变，当前主脚本SHA仍相同。", duplicate)
item("D08", "真实build_patch拒绝固定AccountSession差异；源码逐字节恢复、Player和原发布指针不变、无错误101指针。", NEGATIVE / "result.json", NEGATIVE / "Attempt/patchbuild-report.txt", HERE / "final-audit.json")
item("E01", "当前225内FrameCodec11/PipelineReader11/MessageRouter11覆盖半包、粘包、超长、未知ID、坏protobuf等。", SERVER)
item("E02", "当前69项基础回归内Pending/生命周期/传输超时、取消、旧代次收尾等全部通过。", FOUNDATION)
item("E03", "当前OpIdStore8项覆盖重启持续递增、失败不发号、不同玩家、并发与边界。", FOUNDATION)
item("E04", "当前Idempotency11项覆盖重复执行一次、不同负载拒绝、64项淘汰与TTL等。", SERVER)
item("E05", "复用同一UiManager/资源实现的C1最终27项：真实Lua/Prefab20次开关，生命周期/订阅/资源回基线；当前UIResource再通过。", C1, PLAY)
item("E06", "C1真实SceneService10次切换、取消/失败回Login/Loading与常驻Root通过；当前冷启动和Native场景链再通过。", C1, PLAY, NATIVE / "A04-S1-home100.json")
item("E07", "C1三分辨率与非零safearea测试及4张实际UI截图已Root查看；相关布局代码未改变。", C1, EVIDENCE / "StageC1/Review/shots/shot-1280x720.png", EVIDENCE / "StageC1/Review/shots/shot-1920x1080.png", EVIDENCE / "StageC1/Review/shots/shot-1080x1920.png", EVIDENCE / "StageC1/Review/shots/shot-safearea-nonzero.png")
item("E08", "当前事件/对象池/计时器12项通过，含退订、重复归还、取消和非缩放计时。", FOUNDATION)
item("E09", "当前真实InputSystem设备5项和EventSystem模态点击2项全部通过；移动/点击/拖拽/滚轮与遮挡覆盖。", INPUT)
item("E10", "Native正式滚动client.log及服务端metrics保留；实际请求有session/msg/seq关联；FileLogger滚动/脱敏和Diagnostics测试通过；实际私密值扫描0匹配。", client_log, metric_log, FOUNDATION, SERVER, HERE / "final-audit.json")
item("E11", "当前真实SQLITE_READONLY8失败保持Dirty，恢复可写后重试持久化成功。", SERVER)
item("E12", "真实版本化xlsx导101；当前双端JSON逐文件SHA一致；Native与正式ServerConfigService均读取新值；错误清单/版本/必需键测试通过。", HERE / "r2-config101.stdout", HERE / "server-config101-read.json", NATIVE / "B08-real-prefab-and-global.json", SERVER, HERE / "final-audit.json")

matrix_path = EVIDENCE / "StageE/AcceptanceMatrix.json"
matrix = read(matrix_path)
assert len(matrix["steps"]) == len(mapping) == 58
if not (HERE / "AcceptanceMatrix.before-final.json").exists():
    (HERE / "AcceptanceMatrix.before-final.json").write_bytes(matrix_path.read_bytes())
for step in matrix["steps"]:
    actual, evidence = mapping[step["id"]]
    step.update(status="pass", actual=actual, evidence=evidence)
matrix.update(status="pass", reviewedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
              sourceSha256AtFinalReview=sha(ROOT / "Assets/ReferenceDoc/06_M1_基础框架任务拆解.md"),
              candidate="Root Final 100 R2 + Root Final Publish R2", passed=58, failed=0, pending=0,
              audit="Docs/Acceptance/M1/20261020-xLua/Root-Finalization/final-audit.json")
matrix_path.write_text(json.dumps(matrix, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def link(path, label=None):
    absolute = Path(path).resolve()
    return f"[{label or absolute.name}](<{absolute.as_posix()}>)"


lines = ["# M1 最终验收记录", "", "日期：2026-10-21。范围：Windows x64 标准 IL2CPP / xLua；服务端 .NET 8。", "",
         "**结论：A01–A11、B01–B14、C01–C13、D01–D08、E01–E12，共58项通过。T01–T68完成。**", "",
         "## 1. 唯一最终候选", "", "- AppVersion 0.3.0 / ProtoVersion 1 / bridge m1-bridge-0.3；资源与配置100→101。",
         "- " + link(BASE / "Player/Farming.exe", "100安装程序"),
         "- " + link(BASE / "baseline-manifest.json", "基包清单") + "；" + link(BASE / "baseline-fingerprint.json", "3342文件冻结指纹"),
         "- " + link(PUBLISH, "101发布目录") + "；" + link(HERE / "final-audit.json", "独立核对结果"),
         "- 旧候选和失败证据保留作诊断；最终运行及新旧资源验证均使用上述R2安装。", "",
         "| 安装文件 | 更新前后相同的SHA-256 |", "|---|---|"]
for key, value in before.items():
    lines.append(f"| {key} | `{value.lower()}` |")
lines += ["", "## 2. 实测结果", "", "| 验证 | 结果 |", "|---|---|",
          "| 原生账号/100→101/缓存复用 | 通过，实际下载42→5→0，安装三SHA不变 |",
          "| 原生损坏资源→修复→生产按钮重试 | 通过，仅prepare1、2，无坏候选激活 |",
          "| 原生恢复 | 完整75秒退避；同库恢复/强杀恢复；revision1；204清模型回登录；双客户端顶号；旧Token204 |",
          "| 原生106 | 显示版本不兼容请更新，无无限重连 |",
          "| 服务端NUnit | 225/225，0失败、0跳过；含不同账号并存的RST隔离 |",
          "| 当前受影响PlayMode | 9/9，0失败、0跳过 |",
          "| 当前基础EditMode | 69/69，0失败、0跳过 |",
          "| 当前输入/模态PlayMode | 7/7，0失败、0跳过 |",
          "| 复用未变更的UI/场景及账号事务证据 | C1 27/27，C2 13/13；不重复计入上述当前测试总数 |", "",
          "## 3. 58项逐条证据", "", "| 编号 | 实际结果 | 证据 |", "|---|---|---|"]
for step in matrix["steps"]:
    refs = "；".join(link(ROOT / p) for p in step["evidence"])
    lines.append(f"| {step['id']} | 通过：{step['actual']} | {refs} |")
lines += ["", "## 4. 重现入口", "", "以下脚本包含具体参数、显式超时与拥有进程清理；凭证只存私有测试文件，不写日志或命令行。",
          "构建脚本拒绝覆盖已存在候选，若重新构建应先选择新的输出目录，保留当前冻结证据。", ""]
for name in ("Build-FinalCandidate.ps1", "Run-NativeAccountsAndPatch.ps1", "Run-NativeBadResource.ps1", "Run-NativeRecovery.ps1", "Run-NativeProtocolMismatch.ps1", "Run-FinalAotNegative.ps1", "Run-FinalFoundation.ps1", "Finalize-Acceptance.py"):
    lines.append("- " + link(HERE / name))
lines += ["", "## 5. 问题修复、证据适用范围", "",
          "- 204/106终止分支曾取消自身重连CTS，导致Lua终止通知丢失；修复前真实用例失败，修复后9项回归及R2 Native过期流程通过。",
          "- Host曾重复解绑并将单连接观察错误升级为停机；现Registry唯一清理账号绑定，Host逐项清理连接/路由/心跳并记录非敏感错误。不同账号并存RST测试及225项回归通过。监听/配置/后台服务失败仍保持全局失败退出。",
          "- catalog的CRLF匹配、完整Lua模型编码、Overlay截图、回执消费/跨重启去重等问题已修复，旧失败目录保留。",
          "- Native强杀恢复只承诺最后已提交的revision1；非默认数据由离线夹具调用生产PlayerStore.Save/reopen产生，没有新增运行时管理接口。",
          "- Windows x64为本次验收范围；Android/iOS、后续种植等正式玩法属于后续工作。",
          "- Unity日志保留引擎退出时的分配诊断及许可连接重试信息，未据测试通过宣称全部引擎日志零警告。",
          "- 历史HybridCLR实测原文保留迁移说明；当前结果仅依据xLua实际产物，未替换历史技术名称。", "",
          "## 6. 实际画面", "", "100家园：", "", "![](<" + (NATIVE / "client/shots/100_home.png").as_posix() + ">)", "",
          "同安装更新101后的真实新面板与Global：", "", "![](<" + (NATIVE / "client/shots/101_patch_window.png").as_posix() + ">)", ""]
(EVIDENCE / "M1_Acceptance.md").write_text("\n".join(lines), encoding="utf-8")
print(json.dumps({"passed": 58, "failed": 0, "pending": 0, "sourceFingerprintFiles": 3342,
                  "serverTests": 225, "foundation": 69, "input": 7, "affectedPlayMode": 9}, ensure_ascii=False))
