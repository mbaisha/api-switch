# AI 接口转发系统（API Switch）

> **企业级 AI API 智能中转与负载均衡平台** — 统一聚合多供应商大模型接口，实现协议标准化转发、跨渠道智能负载均衡、模型链路由编排、多租户令牌管理、全链路可观测运维。

本项目提供了一套开箱即用的 **AI 网关中间件**，后端基于 .NET 10 高性能异步架构，前端采用 Vue 3 + Arco Design 专业后台 UI，数据层使用 PostgreSQL + Redis 构建稳定存储与缓存体系，支持 Docker 一键部署。

---

## 技术栈

| 层级         | 技术选型            | 说明                   |
| ---------- | --------------- | -------------------- |
| **前端框架**   | Vue 3 + Vite 6  | 极速开发，热更新             |
| **UI 组件库** | Arco Design Vue | 商务极简风格，适配专业后台        |
| **状态管理**   | Pinia           | 轻量、类型安全              |
| **后端框架**   | .NET 10 Web API | 原生异步 IO，高并发友好        |
| **ORM**    | FreeSql         | 零 SQL 高效 CRUD，自动建表迁移 |
| **主数据库**   | PostgreSQL 16+  | 事务强一致，JSONB 原生支持     |
| **缓存**     | Redis           | 令牌缓存、限流计数、配置缓存       |
| **日志组件**   | Serilog         | 异步落地文件 + 结构化日志       |
| **部署**     | Docker Compose  | 一键编排，跨平台部署           |

***

## 核心功能

### 渠道管理

- 可视化新增/编辑/启用/禁用渠道，配置实时生效
- 同渠道支持**多 API Key**，自动轮询/随机负载分摊
- 独立配置超时时间、SSE 开关、协议类型（Chat / Response）
- 模型级冷却时长自定义

### 智能负载均衡与模型链路由
- **同地址多密钥负载**：同一接口地址下的多个密钥自动分摊请求，避免单密钥限流
- **跨渠道全局负载**：不同渠道配置相同的自定义模型 ID 自动归入同一负载池，突破单供应商限制
- **模型链路由编排**：支持为自定义模型 ID 配置多个上游（渠道 + 原始模型），按权重分发流量，按优先级降级容错
- 故障节点自动剔除 + 冷却期后自动恢复，用户侧无感
- 支持渠道/模型维度权重配置

### 双协议无损转换

- 下游适配 OpenAI Chat Completions / Responses 双协议
- 上游对外同时暴露 `/v1/chat/completions` 与 `/v1/responses` 双接口
- **仅做格式标准化对齐，100% 保留工具调用、MCP 协议、Skill 扩展字段**，不丢失任何原始数据

### 高可用容错体系

- **429 限流**：瞬时终止当前节点，故障转移至其他可用节点
- **500/502/503/504 服务异常**：自动重试 + 故障转移 + 冷却隔离
- **401/403 密钥失效**：自动标记密钥永久失效，剔除负载池
- **网络/SSL/连接异常**：即时故障转移，短时冷却
- **全节点故障兜底**：输出自定义友好文案，不对外暴露原始错误
- nginx 代理层超时保护（600s），保障长连接 SSE 不中断

### 多租户令牌管理

- 后台一键生成唯一调用令牌，支持自定义备注与归属
- 单令牌模型权限白名单，隔离可调用模型
- 日限额/总限额配置，超额自动禁止调用
- 令牌状态管控（启用/禁用）

### 独立计费核算

- 每个令牌独立台账，数据完全隔离
- 精准统计：输入 Token、输出 Token、总 Token 消耗
- 按模型独立定价，自动计算消费金额与剩余额度
- 账单明细查询与对账

### Token 精准统计

- 字符类型感知估算（CJK × 1.5、ASCII × 0.5），精度 \~89%
- 优先使用下游 API 返回的精确 usage 数据
- 上游 API Key 独立用量统计
- 所有统计实时入库，可追溯对账

### 全链路日志监控

- **调用日志**：令牌、IP、模型、渠道、Token 用量、耗时、状态、错误详情、图片资源
- **操作日志**：管理员所有配置修改行为，可溯源追责
- **多条件筛选**：时间、令牌、模型、状态、渠道
- **CSV 导出**：归档对账
- **自定义留存天数**，自动清理过期数据

### 数据看板

- 总调用量 / 今日调用量
- Token 总消耗
- 模型用量排行
- 令牌用量统计
- 异常统计

### 安全防护

- 接口强制令牌鉴权
- JWT 管理后台认证
- IP + 令牌双维度限流防刷
- API Key / 令牌日志自动脱敏
- 支持 nginx 反向代理获取用户真实 IP（X-Forwarded-For）

### 健康检测

- 定时探测各渠道端点可用性
- 实时展示渠道健康状态

### 多模态支持

- 图片资源自动提取与存储（文件系统，非 DB）
- 请求/响应消息与图片关联可追溯
- 支持 `data:image/base64` 与 URL 引用两种格式

### 图片生成转发（文生图 / 图生图 / 多图）

- **下游统一接口**：`POST /v1/images/generations`，兼容 OpenAI Images 协议，并以 `image` 字段扩展支持图生图与多图输入（无需另开 `/v1/images/edits`）
- **上游已对接平台**（各平台接口规则严格对齐，统一转换）：

  | 平台 | SupplierType | 文生图 | 图生图 | 多图 | 备注 |
  |---|---|---|---|---|---|
  | OpenAI / Azure / DeepSeek / Together / Groq / Custom | 对应类型 | ✅ | `image` 字段 | ✅ 数组 | 近透传 |
  | 火山引擎/豆包 Seedream | `VolcEngine` | ✅ | `image` | ✅ 最多10张 | OpenAI 兼容，`size` 支持 `2K`/`2048x2048` |
  | �硅基流动 | `SiliconFlow` | ✅ | `image`/`image2`/`image3` | ✅ 拆字段 | `size`→`image_size`，响应 `images`→`data` |
  | Agnes-Ai | `Agnes` | ✅ | `extra_body.image` | ✅ 数组 | `response_format` 亦塞进 `extra_body` |
  | 魔搭 ModelScope | `ModelScope` | ✅ | `images`(base64 数组) | ✅ 1-3张 | 异步任务模式，URL 参考图自动下载转 base64 |
  | 商汤 SenseNova U1 | `SenseNova` | ✅ | `chat/completions`+`modalities` | ✅ | `size` 限 11 个白名单值，图生图自动包装 |
  | 讯飞星火 | `Xfyun` | ✅ | HiDream 异步 | ✅ | HMAC 签名鉴权（双密钥），三段式请求体 |
  | Gitee AI | `Gitee` | ✅ | `image` | ✅ | OpenAI 兼容 |
  | 阿里云百炼 DashScope | `DashScope` | ✅ | `image` | ✅ | 通义万相，异步任务模式 |

- **数据传输格式**：
  - 生成图片：URL 或 Base64 返回（`response_format: "url"` 或 `"b64_json"`），原样透传不做代理落盘
  - 参考图片：URL 或 Base64 提交（`image` 字段，string 或数组），适配器内部按平台规则转换（如魔搭需 base64 则自动下载 URL 转码）
- **计费兼容**：优先使用上游返回的 `usage.output_tokens`，未返回则按生成张数估算计费
- **讯飞专属配置**：渠道需在「扩展配置」填 `{"appId":"xxx"}`；API Key 的「第二密钥」填 apiSecret（用于 HMAC 签名）

***

## 快速启动

### 前置条件

- Docker & Docker Compose（推荐）
- 或：.NET 10 SDK + Node.js 18+ + PostgreSQL 16+ + Redis

### Docker 一键部署（推荐）

```bash
# 构建并启动全部服务
docker compose up -d

# 查看运行状态
docker compose ps

# 查看日志
docker compose logs -f

# 访问
# 管理后台: http://localhost:8080
# 后端 API: http://localhost:5000
# 默认账号: admin / admin123
```

### 本地开发

**后端**：

```bash
cd backend

# 修改 appsettings.json 中的数据库连接字符串
# 默认: Host=localhost;Port=5432;Database=ai_forward;Username=postgres;Password=postgres

# 启动（首次运行自动建表 + 初始化管理员）
dotnet run
```

**前端**：

```bash
cd frontend

# 安装依赖
npm install

# 启动开发服务器
npm run dev
```

***

## API 对接

### 对外转发接口（需携带用户令牌）

```bash
# Chat Completions
curl http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-custom-model-id",
    "messages": [{"role": "user", "content": "Hello"}]
  }'

# Responses
curl http://localhost:5000/v1/responses \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "your-custom-model-id",
    "input": "Hello"
  }'

# 流式调用（SSE）
curl http://localhost:5000/v1/chat/completions \
  -H "Authorization: Bearer YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"model": "your-custom-model-id", "messages": [{"role": "user", "content": "Hello"}], "stream": true}'
```

### 管理后台 API（需 JWT 认证）

```bash
# 登录获取 Token
curl -X POST http://localhost:5000/api/admin/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin123"}'

# 后续请求携带 JWT
curl http://localhost:5000/api/admin/channels \
  -H "Authorization: Bearer YOUR_JWT_TOKEN"
```

***

## 使用流程

1. **添加渠道** → 在「渠道管理」添加下游 AI 供应商（如 OpenAI、Azure、Anthropic 等）
2. **配置密钥** → 为每个渠道添加 API Key（支持单渠道多密钥）
3. **配置模型映射** → 在「渠道管理」设置渠道「原始模型 ID → 自定义模型 ID」的映射关系
4. **编排模型链**（可选）→ 在「模型链管理」为自定义模型 ID 配置多个上游渠道，设置权重与优先级
5. **生成令牌** → 在「令牌管理」为用户生成访问令牌，配置模型权限和限额
6. **开始使用** → 用户通过 `/v1/chat/completions` 或 `/v1/responses` 携带令牌调用

***

## 全局配置

启动后在管理后台「全局配置」页面可实时修改：

| 配置项                      | 默认值               | 说明             |
| ------------------------ | ----------------- | -------------- |
| global\_cooldown         | 60                | 故障节点冷却时长（秒）    |
| fallback\_message        | 所有可用渠道暂时不可用，请稍后重试 | 全部节点故障时的兜底提示文案 |
| log\_retention\_days     | 30                | 日志自动清理留存天数     |
| rate\_limit\_per\_minute | 60                | 每分钟单 IP 请求限制次数 |

***

## 项目结构

```
├── backend/                      # .NET 10 Web API 后端
│   ├── Common/
│   │   ├── Models/               # 数据模型（Channel、Token、CallLog、ModelChain 等）
│   │   ├── DTOs/                 # 请求/响应数据传输对象
│   │   └── Utils/                # 工具类（IpHelper、TokenEstimator、LocalDateTimeConverter 等）
│   ├── Controllers/              # API 控制器
│   ├── Services/                 # 业务服务层（ForwardEngine、BillingService 等）
│   ├── Repository/               # 数据仓储层（BaseRepository）
│   └── Program.cs                # 应用入口
├── frontend/                     # Vue 3 管理后台
│   ├── src/
│   │   ├── api/                  # API 接口封装
│   │   ├── router/               # 路由配置
│   │   ├── layouts/              # 布局组件（侧边栏、顶栏）
│   │   └── views/admin/          # 后台页面组件
│   └── nginx.conf                # nginx 反向代理配置
├── docker-compose.yml            # Docker 编排配置
└── nginx.conf                    # 前端 nginx 配置
```

***

## 系统架构

```
┌──────────────┐     ┌──────────────┐     ┌──────────────────┐
│   用户/客户端    │────▶│   nginx 反向代理  │────▶│  AI 接口转发服务     │
│  (携带令牌调用)   │     │  (前端静态+代理)  │     │  (.NET 10 Web API)  │
└──────────────┘     └──────────────┘     └───────┬──────────┘
                                                  │
                    ┌───────────────────────────────┼──────────────────┐
                    │                               │                  │
                    ▼                               ▼                  ▼
           ┌──────────────┐              ┌────────────────┐   ┌────────────┐
           │  PostgreSQL    │              │     Redis       │   │ 上游 AI     │
           │  (持久化数据)   │              │  (缓存/限流)     │   │ 供应商      │
           └──────────────┘              └────────────────┘   └────────────┘
```

***

## 注意：项目来源与使用前说明

> 本项目最初为**个人自用**开发的 AI 接口转发工具，旨在满足内部使用场景，并未经过大规模生产环境验证。虽然核心功能已经过基本测试，但在将其用于**生产环境**之前，强烈建议您：
>
> 1. **进行全面的功能测试** — 覆盖渠道配置、负载均衡、令牌鉴权、计费核算、日志记录等所有核心链路
> 2. **执行压力测试与性能评估** — 根据预期并发量评估服务承载能力，必要时调整线程池、连接池等参数
> 3. **审查安全配置** — 修改默认管理员密码、JWT 密钥、数据库连接字符串等敏感配置
> 4. **制定灾备与数据备份方案** — 确保 PostgreSQL 数据库定期备份，防止数据丢失
>
> 本项目以 **MIT 许可证** 开源，仅供学习和研究参考。作者不对因使用本软件而产生的任何直接或间接损失承担责任。

***

## License

MIT License — 详见 [LICENSE](LICENSE) 文件。

***

> **AI 接口转发系统** — 统一入口 · 智能负载 · 模型链路由 · 无损转换 · 全链路可观测

