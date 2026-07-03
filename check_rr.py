#!/usr/bin/env python3
"""验证同一渠道内不同 API Key 是否轮询"""
import json
import urllib.request
import time

URI = "http://localhost:5000/v1/chat/completions"
TOKEN = "sk-0f1192e0d6df4258aa5a08ad20451a51"
BODY = json.dumps({
    "model": "agnes-1.5-flash",
    "messages": [{"role": "user", "content": "hi"}],
    "stream": False
})

def send_req(seq):
    data = BODY.encode('utf-8')
    req = urllib.request.Request(URI, data=data, method='POST')
    req.add_header('Content-Type', 'application/json')
    req.add_header('Authorization', f'Bearer {TOKEN}')
    start = time.time()
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            text = resp.read().decode('utf-8')
            ms = int((time.time() - start) * 1000)
            ok = '"choices"' in text
            print(f"  [#{seq}] {'OK' if ok else 'FAIL'} ({ms}ms)")
            return ok
    except Exception as e:
        ms = int((time.time() - start) * 1000)
        print(f"  [#{seq}] ERR: {str(e)[:60]} ({ms}ms)")
        return False

print("发送 4 次请求，检查日志中使用的 API Key...")
print("请同时观察服务器终端的日志输出")
print()

for i in range(4):
    send_req(i + 1)

print()
print("请查看服务器终端的日志输出，确认：")
print("  - 各次请求使用的 API Key 是否不同")
print("  - `API Key=sk-KxF7` 和 `API Key=sk-lQV1U` 是否交替出现")