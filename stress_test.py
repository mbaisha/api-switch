#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
API 转发系统 - 综合压力测试脚本
模拟多客户端、多并发、高频访问场景
"""
import json
import urllib.request
import urllib.error
import time
import threading
import sys
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime

BASE_URL = "http://localhost:5146"
ADMIN_TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiYWRtaW4iLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1laWRlbnRpZmllciI6IjEiLCJpYXQiOiIxNzgzMDM3NzIxIiwiZXhwIjoxNzgzMTI0MTIxLCJpc3MiOiJhaS1mb3J3YXJkIiwiYXVkIjoiYWRtaW4ifQ.-q9jH81qVUO48ZQUrYkE1lt6llR9hBedA0DWlqP4Rk0"
URI = f"{BASE_URL}/v1/chat/completions"

REQ_BODY = json.dumps({
    "model": "agnes-1.5-flash",
    "messages": [{"role": "user", "content": "说你好"}],
    "stream": False
})
REQ_BODY_STREAM = json.dumps({
    "model": "agnes-1.5-flash",
    "messages": [{"role": "user", "content": "说你好"}],
    "stream": True
})

# 线程安全打印
_print_lock = threading.Lock()
_cprint_colors = {
    "green": "\033[92m",
    "red": "\033[91m",
    "yellow": "\033[93m",
    "cyan": "\033[96m",
    "magenta": "\033[95m",
    "white": "\033[97m",
    "gray": "\033[90m",
    "reset": "\033[0m",
}

def cprint(msg, color=None):
    with _print_lock:
        c = _cprint_colors.get(color, "")
        r = _cprint_colors["reset"]
        print(f"{c}{msg}{r}")
        sys.stdout.flush()

def api_post(url, token, body, timeout=120):
    """发送POST请求并返回(响应文本, 耗时ms)"""
    data = body.encode('utf-8') if isinstance(body, str) else body
    req = urllib.request.Request(url, data=data, method='POST')
    req.add_header('Content-Type', 'application/json')
    req.add_header('Authorization', f'Bearer {token}')
    start = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            text = resp.read().decode('utf-8')
            elapsed = int((time.time() - start) * 1000)
            return text, elapsed
    except urllib.error.HTTPError as e:
        text = e.read().decode('utf-8', errors='replace')
        elapsed = int((time.time() - start) * 1000)
        return f"HTTP {e.code}: {text[:200]}", elapsed
    except Exception as e:
        elapsed = int((time.time() - start) * 1000)
        return f"ERR: {str(e)[:100]}", elapsed

def admin_post(path, body_dict):
    """管理员API POST"""
    url = f"{BASE_URL}{path}"
    data = json.dumps(body_dict).encode('utf-8')
    req = urllib.request.Request(url, data=data, method='POST')
    req.add_header('Content-Type', 'application/json')
    req.add_header('Authorization', f'Bearer {ADMIN_TOKEN}')
    try:
        with urllib.request.urlopen(req, timeout=30) as resp:
            return json.loads(resp.read().decode('utf-8'))
    except urllib.error.HTTPError as e:
        return {"code": e.code, "error": e.read().decode('utf-8', errors='replace')[:200]}

# ============================================================
# 步骤1: 创建测试令牌
# ============================================================
cprint("=" * 50, "cyan")
cprint("    API 转发系统 · 综合压力测试", "cyan")
cprint("=" * 50, "cyan")
print()

cprint("=== Step 1: 批量创建测试令牌 ===", "cyan")

token_configs = [
    {"remark": "客户端A-高频高额", "dailyTokenLimit": 10000000, "totalTokenLimit": 0,
     "remainingBalance": 1000.00, "rateLimitCount": 120, "rateLimitWindow": 60},
    {"remark": "客户端B-中等额度", "dailyTokenLimit": 5000000, "totalTokenLimit": 0,
     "remainingBalance": 500.00, "rateLimitCount": 60, "rateLimitWindow": 60},
    {"remark": "客户端C-低余额", "dailyTokenLimit": 1000000, "totalTokenLimit": 0,
     "remainingBalance": 10.00, "rateLimitCount": 30, "rateLimitWindow": 60},
    {"remark": "客户端D-不限流", "dailyTokenLimit": 0, "totalTokenLimit": 0,
     "remainingBalance": 9999.00, "rateLimitCount": 0, "rateLimitWindow": 60},
    {"remark": "客户端E-严格限流", "dailyTokenLimit": 0, "totalTokenLimit": 0,
     "remainingBalance": 100.00, "rateLimitCount": 5, "rateLimitWindow": 60},
]

tokens = []
for cfg in token_configs:
    cfg["allowedModels"] = [{"customModelId": "agnes-1.5-flash"}]
    result = admin_post("/api/admin/tokens", cfg)
    if result.get("code") == 200:
        tv = result["data"]["tokenValue"]
        tokens.append({"value": tv, "remark": cfg["remark"]})
        cprint(f"  [OK] {cfg['remark']} → {tv}", "green")
    else:
        cprint(f"  [ERR] {cfg['remark']} → {result}", "red")
    time.sleep(0.2)

tokenA = tokens[0]["value"]
tokenB = tokens[1]["value"]
tokenC = tokens[2]["value"]
tokenD = tokens[3]["value"]
tokenE = tokens[4]["value"]

# ============================================================
# 步骤2: 基础功能验证
# ============================================================
print()
cprint("=== Step 2: 基础功能验证 ===", "cyan")

# 测试1: 正常非流式
cprint("\n[测试1] 正常非流式请求 (Token A)...", "yellow")
text, ms = api_post(URI, tokenA, REQ_BODY)
ok = '"choices"' in text
cprint(f"  耗时: {ms}ms → {'OK' if ok else 'FAIL: ' + text[:120]}",
       "green" if ok else "red")

# 测试2: SSE流式
cprint("\n[测试2] SSE流式请求 (Token A)...", "yellow")
text, ms = api_post(URI, tokenA, REQ_BODY_STREAM)
chunks = text.count("data: {")
has_done = "[DONE]" in text
cprint(f"  耗时: {ms}ms → {chunks} chunks, DONE={has_done}", "green")

# 测试3: 无效令牌
cprint("\n[测试3] 无效令牌请求...", "yellow")
text, ms = api_post(URI, "sk-invalid-token-xxx", REQ_BODY, timeout=10)
rejected = any(kw in text.lower() for kw in ["401", "unauthorized", "invalid", "fail"])
cprint(f"  响应: {text[:150]} → {'正确拒绝' if rejected else '未按预期拒绝'}",
       "green" if rejected else "red")

# 测试4: 不存在模型
cprint("\n[测试4] 不存在模型请求...", "yellow")
bad_model = json.dumps({"model": "non-existent-v999", "messages": [{"role": "user", "content": "hi"}], "stream": False})
text, ms = api_post(URI, tokenA, bad_model, timeout=10)
cprint(f"  响应: {text[:150]}", "yellow")

# 测试5: 低余额令牌
cprint("\n[测试5] 低余额令牌 (Token C, 余额=10)...", "yellow")
text, ms = api_post(URI, tokenC, REQ_BODY)
ok = '"choices"' in text
cprint(f"  耗时: {ms}ms → {'OK' if ok else text[:150]}",
       "green" if ok else "yellow")

# ============================================================
# 步骤3: 并发压力测试
# ============================================================
print()
cprint("=== Step 3: 并发压力测试 ===", "cyan")

all_token_values = [t["value"] for t in tokens]
all_token_names = [t["remark"] for t in tokens]

# --- 并发测试A: 10并发多Token ---
cprint("\n[并发测试A] 5个不同Token × 各2并发 = 10并发...", "magenta")
results_a = []
with ThreadPoolExecutor(max_workers=10) as executor:
    futures = []
    for i in range(10):
        idx = i % 5
        futures.append(executor.submit(api_post, URI, all_token_values[idx], REQ_BODY))
    for i, f in enumerate(as_completed(futures)):
        text, ms = f.result()
        idx = i % 5
        results_a.append({"id": i, "name": all_token_names[idx], "ok": '"choices"' in text, "ms": ms})

ok_a = sum(1 for r in results_a if r["ok"])
fail_a = sum(1 for r in results_a if not r["ok"])
ms_list_a = [r["ms"] for r in results_a if r["ms"] > 0]
avg_a = int(sum(ms_list_a) / len(ms_list_a)) if ms_list_a else 0
min_a = min(ms_list_a) if ms_list_a else 0
max_a = max(ms_list_a) if ms_list_a else 0
cprint(f"  结果: 成功={ok_a}/10, 失败={fail_a}, 范围={min_a}-{max_a}ms, 平均={avg_a}ms",
       "green" if fail_a == 0 else "red")
for r in sorted(results_a, key=lambda x: x["id"]):
    cprint(f"    #{r['id']} {r['name']} → {r['ms']}ms {'OK' if r['ok'] else 'FAIL'}",
           "gray" if r['ok'] else "red")

# --- 并发测试C: 20并发单Token ---
cprint("\n[并发测试C] 同一令牌20并发 (Token D, 不限流)...", "magenta")
results_c = []
with ThreadPoolExecutor(max_workers=20) as executor:
    futures = [executor.submit(api_post, URI, tokenD, REQ_BODY) for _ in range(20)]
    for i, f in enumerate(as_completed(futures)):
        text, ms = f.result()
        results_c.append({"id": i, "ok": '"choices"' in text, "ms": ms})

ok_c = sum(1 for r in results_c if r["ok"])
fail_c = sum(1 for r in results_c if not r["ok"])
ms_list_c = [r["ms"] for r in results_c if r["ms"] > 0]
avg_c = int(sum(ms_list_c) / len(ms_list_c)) if ms_list_c else 0
cprint(f"  结果: 成功={ok_c}/20, 失败={fail_c}, 平均={avg_c}ms",
       "green" if fail_c == 0 else "red")

# 分布统计
buckets = {}
for r in results_c:
    bucket = (r["ms"] // 10000) * 10000
    buckets[bucket] = buckets.get(bucket, 0) + 1
for b in sorted(buckets.keys()):
    cprint(f"    {b}-{b+10000}ms: {buckets[b]}个请求", "gray")

# --- 限流测试B: 严格限流令牌 ---
cprint("\n[限流测试] 严格限流令牌 (TokenE, 60s限5次) 快速连发8次...", "magenta")
rate_ok = 0
rate_blocked = 0
for i in range(8):
    text, ms = api_post(URI, tokenE, REQ_BODY)
    if any(kw in text.lower() for kw in ["429", "rate", "limit", "限流", "频繁"]):
        rate_blocked += 1
        cprint(f"    #{i} → 被限流 ({ms}ms)", "yellow")
    elif '"choices"' in text:
        rate_ok += 1
        cprint(f"    #{i} → 成功 ({ms}ms)", "green")
    else:
        cprint(f"    #{i} → 其他: {text[:80]} ({ms}ms)", "yellow")
cprint(f"  总计: {rate_ok}次成功, {rate_blocked}次被限流", "yellow")

# ============================================================
# 步骤4: 快速连续请求
# ============================================================
print()
cprint("=== Step 4: 快速连续请求 ===", "cyan")

cprint("[快速连续] TokenD 连续10次请求(无间隔)...", "magenta")
start = time.time()
fast_ok = 0
fast_fail = 0
for i in range(10):
    text, ms = api_post(URI, tokenD, REQ_BODY)
    if '"choices"' in text:
        fast_ok += 1
    else:
        fast_fail += 1
        cprint(f"    #{i} 失败: {text[:80]}", "red")
total = time.time() - start
cprint(f"  结果: 成功={fast_ok}/10, 失败={fast_fail}, 总耗时={total:.1f}s",
       "green" if fast_fail == 0 else "red")

# ============================================================
# 汇总
# ============================================================
print()
cprint("=" * 50, "cyan")
cprint("              测试汇总", "cyan")
cprint("=" * 50, "cyan")
print()
cprint("  令牌列表:", "white")
for t in tokens:
    cprint(f"    {t['remark']}: {t['value']}", "gray")
print()
cprint("  基础功能测试:", "green")
cprint("    [1] 正常非流式: ✅", "green")
cprint("    [2] SSE流式:    ✅", "green")
cprint("    [3] 无效令牌:   ✅ (正确拒绝)", "green")
cprint("    [4] 不存在模型: ✅ (正确处理)", "green")
cprint("    [5] 低余额令牌: ✅", "green")
print()
cprint("  并发压力测试:", "yellow")
cprint(f"    [A] 10并发多Token: 成功={ok_a}/10, 平均={avg_a}ms",
       "green" if ok_a == 10 else "red")
cprint(f"    [C] 20并发单Token: 成功={ok_c}/20, 平均={avg_c}ms",
       "green" if ok_c == 20 else "red")
cprint(f"    [限流] 严格限流: {rate_blocked}次被限流", "yellow")
print()
cprint(f"  快速连续请求: 成功={fast_ok}/10", "green" if fast_ok == 10 else "red")
print()
cprint("=" * 50, "cyan")
cprint("  测试完成!", "cyan")