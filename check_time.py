import json, urllib.request

# 登录获取 token
login = urllib.request.Request(
    "http://localhost:5000/api/admin/auth/login",
    data=json.dumps({"username": "admin", "password": "admin123"}).encode(),
    headers={"Content-Type": "application/json"},
    method="POST"
)
with urllib.request.urlopen(login) as resp:
    token_data = json.loads(resp.read())
print(f"登录响应: {json.dumps(token_data, indent=2, ensure_ascii=False)[:300]}")
jwt = token_data["data"]["token"]

# 查询渠道列表
channels = urllib.request.Request(
    "http://localhost:5000/api/admin/channels",
    headers={"Authorization": f"Bearer {jwt}"}
)
with urllib.request.urlopen(channels) as resp:
    data = json.loads(resp.read())
    items = data.get("data", []) if isinstance(data, dict) else data
    if isinstance(items, list):
        for ch in items[:5]:
            print(f"  渠道: {ch.get('name','?')}  CreatedAt={ch.get('createdAt','N/A')}")

# 查询令牌列表
tokens = urllib.request.Request(
    "http://localhost:5000/api/admin/tokens",
    headers={"Authorization": f"Bearer {jwt}"}
)
with urllib.request.urlopen(tokens) as resp:
    data = json.loads(resp.read())
    items = data.get("data", []) if isinstance(data, dict) else data
    if isinstance(items, list):
        for tk in items[:5]:
            print(f"  令牌: {tk.get('remark','?')}  CreatedAt={tk.get('createdAt','N/A')}")

# 查询调用日志
logs = urllib.request.Request(
    "http://localhost:5000/api/admin/logs?page=1&pageSize=3",
    headers={"Authorization": f"Bearer {jwt}"}
)
with urllib.request.urlopen(logs) as resp:
    data = json.loads(resp.read())
    items = data.get("data", []) if isinstance(data, dict) else data
    if isinstance(items, list):
        for log in items[:5]:
            print(f"  日志: {log.get('customModelId','?')}  CreatedAt={log.get('createdAt','N/A')}")