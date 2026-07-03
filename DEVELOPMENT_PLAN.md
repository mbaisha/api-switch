# AI接口转发系统 — 开发计划

## 项目概览

| 维度 | 说明 |
|------|------|
| 前端 | Vue3 + Vite + Arco Design Vue + Pinia |
| 后端 | .NET 10 Web API + FreeSql + Serilog + Polly |
| 数据库 | PostgreSQL 16+ |
| 缓存 | Redis |
| 架构 | 轻量DDD + 仓储模式，四层分层 |

---

## Phase 1：基础架构搭建 ✅

**目标**：项目骨架、分层结构、数据库、基础设施就绪

- [x] 1.1 项目初始化 — 创建 .NET 10 Web API 项目 + Vue3+Vite 前端项目，配置 Docker Compose
- [x] 1.2 分层结构 — 建立 Controller → Service → Repository → Common 四层目录结构
- [x] 1.3 数据库设计 — 设计核心表并 FreeSql CodeFirst 建表
- [x] 1.4 ORM 集成 — FreeSql 配置，仓储基类封装
- [x] 1.5 基础设施 — Serilog 日志、统一返回格式
- [x] 1.6 前端骨架 — Vue Router 路由、Arco Design 布局、Pinia 状态管理

---

## Phase 2：渠道管理模块 ✅

**目标**：渠道 CRUD + 模型映射配置，后台全量可视化

- [x] 2.1 渠道 CRUD API
- [x] 2.2 API Key 管理
- [x] 2.3 模型映射 API
- [x] 2.4 前端渠道管理页
- [x] 2.5 配置热加载 — 数据库直读（Redis可后续扩展）

---

## Phase 3：核心转发引擎 ✅

**目标**：请求接入 → 鉴权 → 负载均衡 → 协议转换 → 转发 → 响应返回

- [x] 3.1 对外接口暴露 — `/v1/chat/completions` + `/v1/responses`
- [x] 3.2 令牌鉴权中间件
- [x] 3.3 负载均衡引擎 — 加权随机选择
- [x] 3.4 协议转换层 — Chat↔Response 双向无损转换
- [x] 3.5 HTTP 转发客户端 — HttpClientFactory + SSE 流式透传
- [x] 3.6 故障转移体系 — 分级错误处理 + 冷却机制 + 兜底
- [x] 3.7 Token 统计 — 基础Token统计实现

---

## Phase 4：令牌管理与计费体系 ✅

**目标**：多租户令牌体系 + 独立核算计费

- [x] 4.1 令牌 CRUD API
- [x] 4.2 权限配置 — 模型白名单、限额
- [x] 4.3 计费配置 — 按模型定价 (BillingRule)
- [x] 4.4 账单统计与导出
- [x] 4.5 前端令牌管理页
- [x] 4.6 前端账单页 — 账单记录 + 定价规则管理

---

## Phase 5：日志监控与数据看板 ✅

**目标**：全链路日志 + 运维看板

- [x] 5.1 调用日志记录
- [x] 5.2 操作日志记录
- [x] 5.3 日志查询 API — 多条件筛选 + 分页
- [x] 5.4 日志导出 — CSV 导出
- [x] 5.5 日志自动清理 — BackgroundService 定时清理
- [x] 5.6 前端日志页 — 调用日志 + 操作日志双 Tab
- [x] 5.7 数据看板 — 统计卡片 + 模型/令牌排行

---

## Phase 6：安全与性能增强 ✅

**目标**：安全加固 + 性能优化

- [x] 6.1 管理员登录 — JWT Bearer Token 鉴权
- [x] 6.2 数据脱敏 — API Key / 令牌自动脱敏展示
- [x] 6.3 防刷限流 — IP + Token 双维度滑动窗口限流（内存版）
- [x] 6.4 健康检测 — 渠道端点探测
- [x] 6.5 全局配置页 — 兜底文案、日志留存天数、限流阈值
- [ ] 6.6 暗黑模式 — Arco Design 内置支持（可后续启用）

---

## Phase 7：测试、部署与文档 ✅

**目标**：质量保障 + 一键部署

- [ ] 7.1 单元测试 — 后续补充
- [ ] 7.2 集成测试 — 后续补充
- [ ] 7.3 压力测试 — 后续补充
- [x] 7.4 Docker 部署配置 — Dockerfile + docker-compose.yml
- [x] 7.5 部署文档 — README

---

## 数据库核心表设计

```
Channels          — 渠道（名称、API地址、超时、SSE开关、协议类型、冷却时长、状态）
ApiKeys           — 渠道密钥（渠道ID、Key、权重、状态）
ChannelModels     — 渠道模型映射（渠道ID、原始模型ID、模型名称、自定义模型ID、权重）
Tokens            — 用户令牌（令牌值、备注、状态、日限额、总限额、余额）
TokenModels       — 令牌模型权限（令牌ID、自定义模型ID）
BillingRules      — 计费规则（令牌ID、模型ID、输入单价、输出单价）
BillingRecords    — 账单记录（令牌ID、模型ID、输入Token、输出Token、消费金额）
CallLogs          — 调用日志（令牌、IP、模型、渠道、Token用量、耗时、状态、请求/响应体）
OperationLogs     — 操作日志（操作人、操作类型、目标、内容、IP）
AdminUsers        — 管理员账号
GlobalConfig      — 全局配置（兜底文案、日志留存天数、限流阈值等）
```

---

## 项目文件结构

```
/workspace
├── backend/
│   ├── Common/
│   │   ├── Models/          # 数据实体
│   │   │   ├── Channel.cs
│   │   │   ├── ApiKey.cs
│   │   │   ├── ChannelModel.cs
│   │   │   ├── Token.cs
│   │   │   ├── TokenModel.cs
│   │   │   ├── CallLog.cs
│   │   │   ├── OperationLog.cs
│   │   │   ├── BillingRecord.cs
│   │   │   ├── BillingRule.cs
│   │   │   ├── GlobalConfig.cs
│   │   │   └── AdminUser.cs
│   │   ├── DTOs/
│   │   │   └── ApiResult.cs  # 统一返回格式
│   │   └── Utils/
│   │       ├── DbContext.cs
│   │       ├── AdminAuthAttribute.cs
│   │       └── RateLimitMiddleware.cs
│   ├── Controllers/
│   │   ├── ChannelController.cs   # 渠道管理
│   │   ├── TokenController.cs     # 令牌管理
│   │   ├── ForwardController.cs   # 转发入口 (/v1/*)
│   │   ├── LogController.cs       # 日志查询/导出
│   │   ├── DashboardController.cs # 数据看板
│   │   ├── BillingController.cs   # 计费管理
│   │   ├── AuthController.cs      # 管理员认证
│   │   ├── ConfigController.cs    # 全局配置
│   │   └── HealthController.cs    # 健康检测
│   ├── Services/
│   │   ├── ChannelService.cs      # 渠道服务 (节点池)
│   │   ├── TokenService.cs        # 令牌验证
│   │   ├── ForwardEngine.cs       # 核心转发引擎
│   │   ├── BillingService.cs      # 计费服务
│   │   ├── AuthService.cs         # JWT 认证
│   │   └── LogCleanupService.cs   # 日志清理
│   ├── Repository/
│   │   └── BaseRepository.cs      # 通用仓储
│   ├── Program.cs
│   ├── Dockerfile
│   └── backend.csproj
├── frontend/
│   ├── src/
│   │   ├── api/index.js           # API 封装
│   │   ├── router/index.js        # 路由 + 导航守卫
│   │   ├── layouts/MainLayout.vue # 主布局
│   │   ├── views/
│   │   │   ├── dashboard/Dashboard.vue
│   │   │   └── admin/
│   │   │       ├── Channels.vue
│   │   │       ├── Tokens.vue
│   │   │       ├── Logs.vue
│   │   │       ├── Billing.vue
│   │   │       ├── Settings.vue
│   │   │       └── Login.vue
│   │   ├── App.vue
│   │   └── main.js
│   ├── Dockerfile
│   ├── nginx.conf
│   └── vite.config.js
├── docker-compose.yml
├── DEVELOPMENT_PLAN.md
└── README.md
```
