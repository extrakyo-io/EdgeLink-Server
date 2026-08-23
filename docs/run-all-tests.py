# run-all-tests.py — 把所有測試跑過一遍,合併成一份報告
#
#   1. .NET 單元 + 整合測試        (Server.Tests)
#   2. 平台鏈路全命令測試            (PlatformTcpTestAll)
#   3. 設備鏈路全欄位測試            (RigUdpTestAll)
#   4. 兩條鏈路同時跑                (FullChainTestAll)
#   5. VR 端 PlayMode 測試          (Unity batchmode)
#
# 埠的獨佔關係決定了順序:2/3/4 各自要獨佔模擬器或轉發埠,所以跑那三段時不能有常駐
# 模擬器;第 5 段反過來需要兩個模擬器都在,而且 47811 要空著給 Unity 綁。
# 這支程式負責在正確的時機起停,不用人去記。
#
# 用法:
#   python docs/run-all-tests.py                  # 全部
#   python docs/run-all-tests.py --skip-dotnet    # 跳過 .NET(最花時間的一段)
#   python docs/run-all-tests.py --only unity
#
# 前提:EdgeLink 已跑起來、PlatformTcp 與 RigBinary 兩組埠都設好。
#      .NET 那段用 --no-build,請先 dotnet build 過。

import argparse
import os
import re
import subprocess
import sys
import time
import urllib.request
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)
OUT = os.path.join(HERE, '_testout')

PLATFORM_SIM = os.path.join(HERE, 'PlatformTcpSimulator.py')
RIG_SIM = os.path.join(HERE, 'RigUdpSimulator.py')
UNITY_PROJECT = r'C:\Projects\Server'


def log(msg):
    print(msg, flush=True)


def header(title):
    log("\n" + "─" * 74)
    log("  " + title)
    log("─" * 74)


class Phase:
    def __init__(self, name):
        self.name = name
        self.passed = 0
        self.total = 0
        self.ok = False
        self.note = ''
        self.seconds = 0.0


def py(script, *args, timeout=600):
    env = dict(os.environ, PYTHONIOENCODING='utf-8')
    return subprocess.run([sys.executable, os.path.join(HERE, script), *args],
                          capture_output=True, text=True, encoding='utf-8',
                          errors='replace', env=env, timeout=timeout)


def parse_counts(text):
    """從 '  33/33 通過' 這種行抓出通過數與總數。"""
    m = None
    for line in text.splitlines():
        found = re.search(r'(\d+)\s*/\s*(\d+)\s*通過', line)
        if found:
            m = found
    return (int(m.group(1)), int(m.group(2))) if m else (0, 0)


# ── 環境 ─────────────────────────────────────────────────────────────────────
def edgelink_alive(port=8081):
    try:
        with urllib.request.urlopen(f'http://localhost:{port}/', timeout=3) as r:
            return r.status == 200
    except Exception:
        return False


class Sims:
    """常駐模擬器的起停。只有 Unity 那段需要。"""

    def __init__(self, hz_platform, hz_rig):
        self.hz_platform, self.hz_rig = hz_platform, hz_rig
        self.procs = []

    def start(self):
        env = dict(os.environ, PYTHONIOENCODING='utf-8')
        self.procs = [
            subprocess.Popen([sys.executable, PLATFORM_SIM, '--hz', str(self.hz_platform)],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env),
            subprocess.Popen([sys.executable, RIG_SIM, '--target', '127.0.0.1:47810',
                              '--hz', str(self.hz_rig)],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, env=env),
        ]

    def stop(self):
        for p in self.procs:
            p.terminate()
            try:
                p.wait(timeout=5)
            except subprocess.TimeoutExpired:
                p.kill()
        self.procs = []


def kill_stray_sims():
    """先前留下來的模擬器會佔住 47802 / 47811,讓後面每一段都莫名其妙失敗。"""
    if os.name != 'nt':
        return
    subprocess.run(['powershell', '-NoProfile', '-Command',
                    "Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | "
                    "Where-Object { $_.CommandLine -like '*PlatformTcpSimulator*' -or "
                    "$_.CommandLine -like '*RigUdpSimulator*' } | "
                    "ForEach-Object { Stop-Process -Id $_.ProcessId -Force }"],
                   capture_output=True)
    time.sleep(1.5)


# ── 各段 ─────────────────────────────────────────────────────────────────────
def run_dotnet(args):
    p = Phase(".NET 單元 + 整合")
    header("1/5  .NET 測試(Server.Tests)")
    trx = os.path.join(OUT, 'dotnet.trx')
    t0 = time.time()
    r = subprocess.run(
        ['dotnet', 'test', os.path.join(REPO, 'Server.Tests', 'Server.Tests.csproj'),
         '-c', 'Release', '--no-build',
         '--logger', f'trx;LogFileName={trx}'],
        capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=1800)
    p.seconds = time.time() - t0

    if os.path.exists(trx):
        root = ET.parse(trx).getroot()
        ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        c = root.find('.//t:ResultSummary/t:Counters', ns)
        if c is not None:
            p.total = int(c.get('total', 0))
            p.passed = int(c.get('passed', 0))
    else:
        p.note = 'trx 沒產生(是否忘了先 dotnet build?)'
    p.ok = r.returncode == 0 and p.total > 0 and p.passed == p.total
    log(f"  {p.passed}/{p.total} 通過  ({p.seconds:.0f}s)")
    return p


def run_python_suite(idx, name, script, *script_args):
    p = Phase(name)
    header(f"{idx}/5  {name}")
    t0 = time.time()
    r = py(script, *script_args, timeout=900)
    p.seconds = time.time() - t0
    p.passed, p.total = parse_counts(r.stdout)
    p.ok = r.returncode == 0 and p.total > 0 and p.passed == p.total

    for line in r.stdout.splitlines():
        if 'FAIL' in line or '通過' in line:
            log("  " + line.strip())
    if not p.total:
        p.note = '解析不到結果'
        log("  " + (r.stdout or r.stderr or '')[-400:])
    return p


def find_unity():
    ver_file = os.path.join(UNITY_PROJECT, 'ProjectSettings', 'ProjectVersion.txt')
    version = None
    if os.path.exists(ver_file):
        with open(ver_file, encoding='utf-8') as f:
            for line in f:
                if line.startswith('m_EditorVersion:'):
                    version = line.split(':', 1)[1].strip()
                    break
    if not version:
        return None, None
    exe = rf'C:\Program Files\Unity\Hub\Editor\{version}\Editor\Unity.exe'
    return (exe, version) if os.path.exists(exe) else (None, version)


def run_unity(args):
    p = Phase("VR 端 PlayMode(Unity)")
    header("5/5  Unity PlayMode 測試")

    exe, version = find_unity()
    if not exe:
        p.note = f'找不到 Unity {version or "?"} —— 跳過'
        log("  " + p.note)
        return p

    sims = Sims(args.platform_hz, args.rig_hz)
    log(f"  起模擬器(平台 {args.platform_hz:g} Hz、設備 {args.rig_hz:g} Hz),等 EdgeLink 撥號…")
    sims.start()
    time.sleep(args.sim_settle)

    results = os.path.join(OUT, 'unity-results.xml')
    unity_log = os.path.join(OUT, 'unity.log')
    for f in (results, unity_log):
        if os.path.exists(f):
            os.remove(f)

    log(f"  Unity {version} batchmode…")
    t0 = time.time()
    try:
        subprocess.run([exe, '-batchmode', '-nographics', '-projectPath', UNITY_PROJECT,
                        '-runTests', '-testPlatform', 'PlayMode',
                        '-testResults', results, '-logFile', unity_log],
                       capture_output=True, timeout=1800)
    finally:
        sims.stop()
    p.seconds = time.time() - t0

    if not os.path.exists(results):
        p.note = f'沒產生 results.xml —— 看 {unity_log}'
        log("  " + p.note)
        return p

    root = ET.parse(results).getroot()
    p.total = int(root.get('total', 0))
    p.passed = int(root.get('passed', 0))
    skipped = int(root.get('skipped', 0))
    p.ok = p.total > 0 and p.passed == p.total

    for tc in root.iter('test-case'):
        res = tc.get('result')
        mark = {'Passed': 'PASS', 'Failed': 'FAIL', 'Skipped': 'SKIP'}.get(res, res)
        log(f"    {mark}  {tc.get('name')}")
        if res != 'Passed':
            msg = tc.find('.//message')
            if msg is not None and msg.text:
                log("          " + msg.text.strip().splitlines()[0])
    if skipped:
        p.note = f'{skipped} 條被跳過(環境未就緒)'
    log(f"  {p.passed}/{p.total} 通過  ({p.seconds:.0f}s)")
    return p


# ── 主流程 ───────────────────────────────────────────────────────────────────
def main():
    ap = argparse.ArgumentParser(description="EdgeLink 全鏈路測試總跑")
    ap.add_argument('--skip-dotnet', action='store_true')
    ap.add_argument('--skip-unity', action='store_true')
    ap.add_argument('--only', choices=['dotnet', 'platform', 'rig', 'fullchain', 'unity'])
    ap.add_argument('--platform-hz', type=float, default=100.0)
    ap.add_argument('--rig-hz', type=float, default=50.0)
    ap.add_argument('--sim-settle', type=float, default=10.0,
                    help="起模擬器後等 EdgeLink 重新撥號的秒數")
    args = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)

    if not edgelink_alive():
        log("EdgeLink 沒有回應 http://localhost:8081/ —— 先把它跑起來")
        return 2

    def want(name):
        return args.only == name if args.only else True

    kill_stray_sims()
    phases = []
    t0 = time.time()

    if want('dotnet') and not args.skip_dotnet:
        phases.append(run_dotnet(args))
    if want('platform'):
        phases.append(run_python_suite(2, "平台鏈路全命令", 'PlatformTcpTestAll.py',
                                       '--hz', str(args.platform_hz)))
    if want('rig'):
        phases.append(run_python_suite(3, "設備鏈路全欄位", 'RigUdpTestAll.py'))
    if want('fullchain'):
        phases.append(run_python_suite(4, "兩條鏈路同時跑", 'FullChainTestAll.py',
                                       '--plat-hz', str(args.platform_hz),
                                       '--rig-hz', str(args.rig_hz)))
    if want('unity') and not args.skip_unity:
        phases.append(run_unity(args))

    kill_stray_sims()

    log("\n" + "═" * 74)
    log("  總結")
    log("═" * 74)
    total_p = total_t = 0
    for p in phases:
        total_p += p.passed
        total_t += p.total
        mark = 'OK  ' if p.ok else 'FAIL'
        extra = f"  {p.note}" if p.note else ''
        log(f"  {mark}  {p.name:22} {p.passed:4}/{p.total:<4} {p.seconds:6.0f}s{extra}")
    log("─" * 74)
    log(f"        {'合計':22} {total_p:4}/{total_t:<4} {time.time() - t0:6.0f}s")
    log("═" * 74)

    return 0 if all(p.ok for p in phases) else 1


if __name__ == '__main__':
    sys.exit(main())
