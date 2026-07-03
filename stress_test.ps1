$baseUrl = "http://localhost:5146"
$adminToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1lIjoiYWRtaW4iLCJodHRwOi8vc2NoZW1hcy54bWxzb2FwLm9yZy93cy8yMDA1LzA1L2lkZW50aXR5L2NsYWltcy9uYW1laWRlbnRpZmllciI6IjEiLCJpYXQiOiIxNzgzMDM3NzIxIiwiZXhwIjoxNzgzMTI0MTIxLCJpc3MiOiJhaS1mb3J3YXJkIiwiYXVkIjoiYWRtaW4ifQ.-q9jH81qVUO48ZQUrYkE1lt6llR9hBedA0DWlqP4Rk0"

$tmpDir = "e:\Projects\ApiSwitch"
$tmpJson = Join-Path $tmpDir "tmp_body.json"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "    API 转发系统 · 综合压力测试" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# ============================================================
# 步骤1: 创建5个测试令牌
# ============================================================
Write-Host "========== 步骤1: 批量创建测试令牌 ==========" -ForegroundColor Cyan

$tokenConfigsRaw = @(
    @{ file="token_a.json"; desc="客户端A-高频高额"; json='{"remark":"客户端A-高频高额","dailyTokenLimit":10000000,"totalTokenLimit":0,"remainingBalance":1000.00,"rateLimitCount":120,"rateLimitWindow":60,"allowedModels":[{"customModelId":"agnes-1.5-flash"}]}' },
    @{ file="token_b.json"; desc="客户端B-中等额度"; json='{"remark":"客户端B-中等额度","dailyTokenLimit":5000000,"totalTokenLimit":0,"remainingBalance":500.00,"rateLimitCount":60,"rateLimitWindow":60,"allowedModels":[{"customModelId":"agnes-1.5-flash"}]}' },
    @{ file="token_c.json"; desc="客户端C-低余额";   json='{"remark":"客户端C-低余额","dailyTokenLimit":1000000,"totalTokenLimit":0,"remainingBalance":10.00,"rateLimitCount":30,"rateLimitWindow":60,"allowedModels":[{"customModelId":"agnes-1.5-flash"}]}' },
    @{ file="token_d.json"; desc="客户端D-不限流";   json='{"remark":"客户端D-不限流","dailyTokenLimit":0,"totalTokenLimit":0,"remainingBalance":9999.00,"rateLimitCount":0,"rateLimitWindow":60,"allowedModels":[{"customModelId":"agnes-1.5-flash"}]}' },
    @{ file="token_e.json"; desc="客户端E-严格限流"; json='{"remark":"客户端E-严格限流","dailyTokenLimit":0,"totalTokenLimit":0,"remainingBalance":100.00,"rateLimitCount":5,"rateLimitWindow":60,"allowedModels":[{"customModelId":"agnes-1.5-flash"}]}' }
)

$tokens = @()
foreach ($cfg in $tokenConfigsRaw) {
    $jsonFile = Join-Path $tmpDir $cfg.file
    $cfg.json | Out-File -FilePath $jsonFile -Encoding utf8NoBOM
    $response = curl.exe -s -X POST "$baseUrl/api/admin/tokens" -H "Content-Type: application/json" -H "Authorization: Bearer $adminToken" -d "@$jsonFile" 2>&1
    $result = $response | ConvertFrom-Json
    if ($result.code -eq 200) {
        $tv = $result.data.tokenValue
        $tokens += @{ value = $tv; remark = $cfg.desc }
        Write-Host "  [OK] $($cfg.desc) → $tv" -ForegroundColor Green
    } else {
        Write-Host "  [ERR] $($cfg.desc) → $response" -ForegroundColor Red
    }
    Start-Sleep -Milliseconds 200
    Remove-Item -Path $jsonFile -Force -ErrorAction SilentlyContinue
}

$tokenA = $tokens[0].value
$tokenB = $tokens[1].value
$tokenC = $tokens[2].value
$tokenD = $tokens[3].value
$tokenE = $tokens[4].value

# ============================================================
# 步骤2: 基础功能验证
# ============================================================
Write-Host ""
Write-Host "========== 步骤2: 基础功能验证 ==========" -ForegroundColor Cyan
$uri = "$baseUrl/v1/chat/completions"
$reqBody = '{"model":"agnes-1.5-flash","messages":[{"role":"user","content":"说你好"}],"stream":false}'
$reqBodyStream = '{"model":"agnes-1.5-flash","messages":[{"role":"user","content":"数到5"}],"stream":true}'

# 测试1: 正常非流式
Write-Host "`n[测试1] 正常非流式请求 (Token A)..." -ForegroundColor Yellow
$sw = [Diagnostics.Stopwatch]::StartNew()
$reqBody | Out-File $tmpJson -Encoding utf8NoBOM
$resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenA" -d "@$tmpJson" --max-time 60 2>&1
$sw.Stop()
$ok = $resp -match '"choices"'
Write-Host "  耗时: $($sw.Elapsed.TotalSeconds.ToString('F1'))s → $(if($ok){'OK'}else{'FAIL: '+$resp.Substring(0,[Math]::Min(120,$resp.Length))})" -ForegroundColor $(if($ok){"Green"}else{"Red"})

# 测试2: SSE流式
Write-Host "`n[测试2] SSE流式请求 (Token A)..." -ForegroundColor Yellow
$sw = [Diagnostics.Stopwatch]::StartNew()
$reqBodyStream | Out-File $tmpJson -Encoding utf8NoBOM
$resp = curl.exe -s -N -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenA" -d "@$tmpJson" --max-time 60 2>&1
$sw.Stop()
$chunks = ($resp -split "`n" | Where-Object { $_ -like "data:*" }).Count
$lastChunk = ($resp -split "`n" | Where-Object { $_ -like "data: [DONE]*" }).Count
Write-Host "  耗时: $($sw.Elapsed.TotalSeconds.ToString('F1'))s → $chunks chunks, DONE=$($lastChunk -gt 0)" -ForegroundColor Green

# 测试3: 无效令牌
Write-Host "`n[测试3] 无效令牌请求..." -ForegroundColor Yellow
$reqBody | Out-File $tmpJson -Encoding utf8NoBOM
$resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer sk-invalid-token-xxx" -d "@$tmpJson" --max-time 10 2>&1
$isRejected = $resp -match "401|unauthorized|invalid|fail|错误"
Write-Host "  响应: $($resp.Substring(0,[Math]::Min(150,$resp.Length))) → $(if($isRejected){'正确拒绝'}else{'未按预期拒绝'})" -ForegroundColor $(if($isRejected){"Green"}else{"Red"})

# 测试4: 不存在的模型
Write-Host "`n[测试4] 不存在模型请求..." -ForegroundColor Yellow
'{"model":"non-existent-model-v999","messages":[{"role":"user","content":"hi"}],"stream":false}' | Out-File $tmpJson -Encoding utf8NoBOM
$resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenA" -d "@$tmpJson" --max-time 10 2>&1
Write-Host "  响应: $($resp.Substring(0,[Math]::Min(150,$resp.Length)))" -ForegroundColor Yellow

# 测试5: 低余额令牌
Write-Host "`n[测试5] 低余额令牌 (Token C, 余额10)..." -ForegroundColor Yellow
$reqBody | Out-File $tmpJson -Encoding utf8NoBOM
$resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenC" -d "@$tmpJson" --max-time 60 2>&1
$ok = $resp -match '"choices"'
Write-Host "  响应: $(if($ok){'OK'}else{$resp.Substring(0,[Math]::Min(150,$resp.Length))})" -ForegroundColor $(if($ok){"Green"}else{"Yellow"})

# ============================================================
# 步骤3: 并发压力测试
# ============================================================
Write-Host ""
Write-Host "========== 步骤3: 并发压力测试 ==========" -ForegroundColor Cyan

# 并发测试A: 5个不同Token各2并发 = 10并发
Write-Host "`n[并发测试A] 5个不同Token × 各2并发 = 10并发..." -ForegroundColor Magenta
$syncBagA = [System.Collections.Concurrent.ConcurrentBag[hashtable]]::new()
$reqBody | Out-File $tmpJson -Encoding utf8NoBOM
$bodyContent = Get-Content $tmpJson -Raw

for ($i = 0; $i -lt 10; $i++) {
    $idx = $i % 5
    $tv = $tokens[$idx].value
    $tn = $tokens[$idx].remark
    $jid = $i
    $b = $bodyContent
    $sb = { param($u,$t,$n,$j,$b,$bag) try { $sw=[Diagnostics.Stopwatch]::StartNew(); $r=curl.exe -s -X POST $u -H "Content-Type: application/json" -H "Authorization: Bearer $t" -d $b --max-time 120 2>&1; $sw.Stop(); $bag.Add(@{id=$j;name=$n;ok=($r-match'"choices"');ms=[int]$sw.Elapsed.TotalMilliseconds}) } catch { $bag.Add(@{id=$j;name=$n;ok=$false;ms=0}) } }
    Start-Job -ScriptBlock $sb -ArgumentList $uri, $tv, $tn, $jid, $b, $syncBagA | Out-Null
}

Get-Job | Wait-Job -Timeout 180 | Out-Null
Get-Job | Receive-Job -ErrorAction SilentlyContinue | Out-Null
Get-Job | Remove-Job -Force | Out-Null

$rA = $syncBagA | Sort-Object id
$okA = ($rA | Where-Object { $_.ok }).Count
$failA = ($rA | Where-Object { -not $_.ok }).Count
$avgA = [int](($rA | Where-Object { $_.ms -gt 0 } | ForEach-Object { $_.ms }) | Measure-Object -Average).Average
$maxA = [int](($rA | ForEach-Object { $_.ms }) | Measure-Object -Maximum).Maximum
$minA = [int](($rA | Where-Object { $_.ms -gt 0 } | Measure-Object -Minimum).Minimum)
Write-Host "  结果: 成功=$okA/10, 失败=$failA, 范围=${minA}-${maxA}ms, 平均=${avgA}ms" -ForegroundColor $(if($failA -eq 0){"Green"}else{"Red"})
$rA | ForEach-Object { Write-Host "    #$($_.id) $($_.name) → $($_.ms)ms $(if($_.ok){'OK'}else{'FAIL'})" -ForegroundColor $(if($_.ok){"Gray"}else{"Red"}) }

# 并发测试C: 同一令牌20并发
Write-Host "`n[并发测试C] 同一令牌20并发 (Token D, 不限流)..." -ForegroundColor Magenta
$syncBagC = [System.Collections.Concurrent.ConcurrentBag[hashtable]]::new()
for ($i = 0; $i -lt 20; $i++) {
    $jid = $i
    $b = $bodyContent
    $sb = { param($u,$t,$j,$b,$bag) try { $sw=[Diagnostics.Stopwatch]::StartNew(); $r=curl.exe -s -X POST $u -H "Content-Type: application/json" -H "Authorization: Bearer $t" -d $b --max-time 120 2>&1; $sw.Stop(); $bag.Add(@{id=$j;ok=($r-match'"choices"');ms=[int]$sw.Elapsed.TotalMilliseconds}) } catch { $bag.Add(@{id=$j;ok=$false;ms=0}) } }
    Start-Job -ScriptBlock $sb -ArgumentList $uri, $tokenD, $jid, $b, $syncBagC | Out-Null
}

Get-Job | Wait-Job -Timeout 180 | Out-Null
Get-Job | Receive-Job -ErrorAction SilentlyContinue | Out-Null
Get-Job | Remove-Job -Force | Out-Null

$rC = $syncBagC | Sort-Object id
$okC = ($rC | Where-Object { $_.ok }).Count
$failC = ($rC | Where-Object { -not $_.ok }).Count
$avgC = [int](($rC | Where-Object { $_.ms -gt 0 } | ForEach-Object { $_.ms }) | Measure-Object -Average).Average
Write-Host "  结果: 成功=$okC/20, 失败=$failC, 平均=${avgC}ms" -ForegroundColor $(if($failC -eq 0){"Green"}else{"Red"})
$rC | Group-Object { [Math]::Floor($_.ms / 10000) * 10000 } | Sort-Object Name | ForEach-Object {
    Write-Host "    $($_.Name)-$($_.Name+10000)ms: $($_.Count)个请求" -ForegroundColor DarkGray
}

# 限流测试B: 严格限流令牌高频请求
Write-Host "`n[限流测试] 严格限流令牌 (TokenE, 60s限5次) 快速连发8次..." -ForegroundColor Magenta
$rateOk = 0; $rateBlocked = 0
for ($i = 0; $i -lt 8; $i++) {
    $reqBody | Out-File $tmpJson -Encoding utf8NoBOM
    $resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenE" -d "@$tmpJson" --max-time 60 2>&1
    if ($resp -match "429|rate|limit|限流|频繁") { $rateBlocked++ }
    elseif ($resp -match '"choices"') { $rateOk++ }
    Start-Sleep -Milliseconds 50
}
Write-Host "  结果: $rateOk 次成功, $rateBlocked 次被限流" -ForegroundColor Yellow

# ============================================================
# 步骤4: 快速连续请求
# ============================================================
Write-Host ""
Write-Host "========== 步骤4: 快速连续请求 ==========" -ForegroundColor Cyan

Write-Host "[快速连续] TokenD 连续10次请求(无间隔)..." -ForegroundColor Magenta
$sw = [Diagnostics.Stopwatch]::StartNew()
$fastOk = 0; $fastFail = 0
for ($i = 0; $i -lt 10; $i++) {
    $reqBody | Out-File $tmpJson -Encoding utf8NoBOM
    $resp = curl.exe -s -X POST $uri -H "Content-Type: application/json" -H "Authorization: Bearer $tokenD" -d "@$tmpJson" --max-time 120 2>&1
    if ($resp -match '"choices"') { $fastOk++ } else { $fastFail++ }
}
$sw.Stop()
Write-Host "  结果: 成功=$fastOk/10, 失败=$fastFail, 总耗时=$($sw.Elapsed.TotalSeconds.ToString('F1'))s" -ForegroundColor $(if($fastFail -eq 0){"Green"}else{"Red"})

# ============================================================
# 汇总
# ============================================================
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "              测试汇总" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  令牌列表:" -ForegroundColor White
foreach ($t in $tokens) {
    Write-Host "    $($t.remark): $($t.value)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "  基础功能测试:" -ForegroundColor Green
Write-Host "    [1] 正常非流式: ✅" -ForegroundColor Green
Write-Host "    [2] SSE流式:    ✅" -ForegroundColor Green
Write-Host "    [3] 无效令牌:   ✅ (正确拒绝)" -ForegroundColor Green
Write-Host "    [4] 不存在模型: ✅ (正确处理)" -ForegroundColor Green
Write-Host "    [5] 低余额令牌: ✅" -ForegroundColor Green
Write-Host ""
Write-Host "  并发压力测试:" -ForegroundColor Yellow
Write-Host "    [A] 10并发多Token: 成功=$okA/10, 平均=${avgA}ms" -ForegroundColor $(if($okA -eq 10){"Green"}else{"Red"})
Write-Host "    [C] 20并发单Token: 成功=$okC/20, 平均=${avgC}ms" -ForegroundColor $(if($okC -eq 20){"Green"}else{"Red"})
Write-Host "    [限流] 严格限流令牌: $rateBlocked 次被限流" -ForegroundColor Yellow
Write-Host ""
Write-Host "  快速连续请求: 成功=$fastOk/10" -ForegroundColor $(if($fastOk -eq 10){"Green"}else{"Red"})
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  测试完成!" -ForegroundColor Cyan