<template>
  <div class="channels-page">
    <div class="page-header">
      <div class="header-left">
        <h2>渠道管理</h2>
        <a-button style="margin-left: 16px" @click="showTestPanel = !showTestPanel" :type="showTestPanel ? 'primary' : 'outline'">
          <template #icon><icon-experiment /></template>
          模型测试
        </a-button>
      </div>
      <a-button type="primary" size="large" @click="showCreateWizard">
        <template #icon><icon-plus /></template>
        新增渠道
      </a-button>
    </div>

    <!-- 渠道列表 -->
    <a-card :bordered="false" class="table-card">
      <a-table :columns="columns" :data="channels" row-key="id" :loading="loading" :pagination="false">
        <template #supplierType="{ record }">
          <a-tag :color="getSupplierColor(record.supplierType)" size="small">
            {{ getSupplierName(record.supplierType) }}
          </a-tag>
        </template>
        <template #enabled="{ record }">
          <a-switch :model-value="record.enabled" size="small" @change="(v) => toggleChannel(record, v)" />
        </template>
        <template #sseEnabled="{ record }">
          <a-tag :color="record.sseEnabled ? 'arcoblue' : 'gray'" size="small">
            {{ record.sseEnabled ? 'SSE' : '-' }}
          </a-tag>
        </template>
        <template #protocolType="{ record }">
          <a-tag v-if="getPreset(record.supplierType)?.isOpenAIProtocol" :color="record.protocolType === 'Chat' ? 'green' : record.protocolType === 'Messages' ? 'orange' : 'purple'" size="small">
            {{ record.protocolType === 'Chat' ? 'Chat' : record.protocolType === 'Messages' ? 'Messages' : 'Response' }}
          </a-tag>
          <span v-else style="color: var(--color-text-3); font-size: 12px">-</span>
        </template>
        <template #modelCount="{ record }">
          <a-tag color="arcoblue" size="small">{{ record._modelCount ?? 0 }} 个模型</a-tag>
        </template>
        <template #action="{ record }">
          <a-space>
            <a-button size="small" type="text" @click="editChannel(record)">编辑</a-button>
            <a-button size="small" type="text" @click="manageChannelDetail(record)">管理</a-button>
            <a-popconfirm content="确认删除此渠道及所有关联数据?" @ok="deleteChannel(record.id)">
              <a-button size="small" type="text" status="danger">删除</a-button>
            </a-popconfirm>
          </a-space>
        </template>
      </a-table>
    </a-card>

    <!-- ===== 新建/编辑渠道向导 ===== -->
    <a-modal v-model:visible="wizardVisible" :title="editingChannelId ? '编辑渠道' : '新增渠道'" width="720px" :footer="false" :mask-closable="false">
      <a-steps :current="wizardStep" size="small" style="margin-bottom: 24px">
        <a-step v-for="(s, i) in wizardSteps" :key="i" :title="s.title" :status="s.status" />
      </a-steps>

      <!-- Step 0: 选择供应商 (仅新建) -->
      <div v-if="wizardStep === 0 && !editingChannelId">
        <div class="supplier-grid">
          <div
            v-for="preset in supplierPresets"
            :key="preset.type"
            :class="['supplier-card', { active: wizardForm.supplierType === preset.type }]"
            @click="selectSupplier(preset)"
          >
            <div class="supplier-icon" :style="{ background: getSupplierColor(preset.type) }">{{ preset.name[0] }}</div>
            <div class="supplier-name">{{ preset.name }}</div>
            <div class="supplier-paths">
              <a-tag v-if="preset.isOpenAIProtocol" size="small" color="green">OpenAI 兼容</a-tag>
              <a-tag v-else size="small" color="orange">自有协议</a-tag>
            </div>
          </div>
        </div>
      </div>

      <!-- Step 0(edit) / Step 1(create): 配置参数 -->
      <a-form v-if="isConfigStep" :model="wizardForm" layout="vertical">
        <a-form-item label="渠道名称" required>
          <a-input v-model="wizardForm.name" placeholder="如：OpenAI 生产环境" />
        </a-form-item>

        <a-form-item label="API 地址 (Base URL)" required>
          <a-input v-model="wizardForm.apiAddress" :placeholder="getApiPlaceholder()" />
          <div class="form-hint">
            <strong>仅需填写 Base URL</strong>，系统将自动根据供应商类型追加接口路径（如 /chat/completions）。
            {{ getApiHint() }}
          </div>
        </a-form-item>

        <!-- 仅 OpenAI 兼容供应商显示协议选择 -->
        <template v-if="currentPreset?.isOpenAIProtocol">
          <a-row :gutter="16">
            <a-col :span="12">
              <a-form-item label="暴露端口（下游可调用）">
                <a-checkbox-group v-model="wizardForm._supportedPathList">
                  <a-checkbox value="chat">/v1/chat/completions</a-checkbox>
                  <a-checkbox value="responses">/v1/responses</a-checkbox>
                  <a-checkbox value="messages">/v1/messages</a-checkbox>
                </a-checkbox-group>
              </a-form-item>
            </a-col>
            <a-col :span="12">
              <a-form-item label="透传（直通不降级）">
                <a-checkbox-group v-model="wizardForm._passthroughPathList">
                  <a-checkbox value="chat">/v1/chat/completions</a-checkbox>
                  <a-checkbox value="responses">/v1/responses</a-checkbox>
                  <a-checkbox value="messages">/v1/messages</a-checkbox>
                </a-checkbox-group>
              </a-form-item>
            </a-col>
          </a-row>
          <a-row :gutter="16">
            <a-col :span="12">
              <a-form-item label="降级目标（未勾选透传时使用）">
                <a-radio-group v-model="wizardForm.fallbackTarget">
                  <a-radio value="Chat">Chat</a-radio>
                  <a-radio value="Response">Response</a-radio>
                  <a-radio value="Messages">Messages</a-radio>
                </a-radio-group>
              </a-form-item>
            </a-col>
          </a-row>
        </template>
        <template v-else>
          <a-alert type="info" style="margin-bottom: 16px">
            此供应商使用自有 API 协议，系统将自动在转发时完成 OpenAI 格式与自有协议的双向转换。
          </a-alert>
        </template>

        <a-row :gutter="16">
          <a-col :span="8">
            <a-form-item label="超时(秒)">
              <a-input-number v-model="wizardForm.timeoutSeconds" :min="5" :max="300" style="width:100%" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="冷却(秒)">
              <a-input-number v-model="wizardForm.cooldownSeconds" :min="0" :max="3600" style="width:100%" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="SSE 流式">
              <a-switch v-model="wizardForm.sseEnabled" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-form-item label="备注">
          <a-textarea v-model="wizardForm.remark" placeholder="可选：渠道备注信息" :rows="2" />
        </a-form-item>
      </a-form>

      <!-- 添加模型 & API密钥 (create step 2 / edit step 1) -->
      <div v-if="isModelStep">
        <a-tabs>
          <a-tab-pane key="keys" title="API 密钥">
            <!-- 编辑时：列出已有密钥 -->
            <div v-if="editingChannelId && existingKeys.length > 0" style="margin-bottom: 16px">
              <div class="batch-header">
                <span>已有密钥 ({{ existingKeys.length }} 个)</span>
              </div>
              <div class="existing-list">
                <div v-for="key in existingKeys" :key="key.id" class="existing-item">
                  <code class="key-text">{{ key.keyValue }}</code>
                  <a-button size="mini" type="text" status="danger" @click="removeExistingKey(key.id)">✕</a-button>
                </div>
              </div>
            </div>
            <!-- 新增密钥 -->
            <div class="batch-section">
              <div class="batch-header">
                <span>{{ editingChannelId ? '新增密钥（每行一个）' : '每行一个密钥，支持多行粘贴' }}</span>
                <a-button size="small" type="text" @click="wizardForm.apiKeys = []; keysTextarea = ''">清空</a-button>
              </div>
              <a-textarea
                v-model="keysTextarea"
                placeholder="sk-xxxxxxx1&#10;sk-xxxxxxx2&#10;sk-xxxxxxx3"
                :rows="6"
              />
              <div class="batch-count">已识别 {{ parsedKeysCount }} 个密钥</div>
            </div>
          </a-tab-pane>
          <a-tab-pane key="models" title="模型映射">
            <!-- 编辑时：列出已有模型 -->
            <div v-if="editingChannelId && existingModels.length > 0" style="margin-bottom: 16px">
              <div class="batch-header">
                <span>已有模型 ({{ existingModels.length }} 个)</span>
              </div>
              <div class="existing-list">
                <div v-for="model in existingModels" :key="model.id" class="existing-item">
                  <div class="existing-model-info">
                    <code class="model-id">{{ model.originalModelId }}</code>
                    <span class="model-arrow">→</span>
                    <code class="model-custom">{{ model.customModelId }}</code>
                    <span v-if="model.modelName" class="model-name">{{ model.modelName }}</span>
                  </div>
                  <a-button size="mini" type="text" status="danger" @click="removeExistingModel(model.id)">✕</a-button>
                </div>
              </div>
            </div>

            <!-- 新增模型 -->
            <div class="batch-section">
              <div class="batch-header">
                <span>为下面添加的模型统一指定「对外暴露的自定义模型ID」（将作为负载均衡池标识）</span>
              </div>
              <div style="display:flex;align-items:center;gap:8px;margin-bottom:12px">
                <span style="font-size:12px;color:var(--color-text-2);white-space:nowrap">统一自定义模型ID：</span>
                <a-input v-model="modelBatchCustomId" placeholder="如: gpt-4o" style="width: 220px" size="small" />
              </div>

              <!-- 供应商推荐模型（勾选式） -->
              <div v-if="wizardForm._availableModels.length > 0" class="model-section-title">
                推荐模型（勾选即添加）
              </div>
              <div v-if="wizardForm._availableModels.length > 0" class="model-preset-list">
                <a-checkbox-group v-model="selectedPresetModels" direction="vertical">
                  <a-checkbox v-for="m in wizardForm._availableModels" :key="m" :value="m">
                    <span class="model-preset-item">
                      <code class="model-id">{{ m }}</code>
                      <span class="model-arrow">→</span>
                      <code class="model-custom">{{ modelBatchCustomId || m }}</code>
                    </span>
                  </a-checkbox>
                </a-checkbox-group>
              </div>

              <!-- 手动输入 -->
              <div class="model-section-title" style="margin-top: 16px">手动添加模型</div>
              <div class="manual-model-input">
                <a-input
                  v-model="manualModelInput"
                  placeholder="输入原始模型 ID，如: gpt-4o-mini"
                  size="small"
                  @keydown.enter.prevent="addManualModel"
                />
                <a-button size="small" type="outline" @click="addManualModel" :disabled="!manualModelInput.trim()">添加</a-button>
              </div>
              <div v-if="manualModels.length > 0" class="manual-model-tags">
                <a-tag
                  v-for="(m, idx) in manualModels"
                  :key="idx"
                  closable
                  size="small"
                  @close="manualModels.splice(idx, 1)"
                >
                  {{ m }} → {{ modelBatchCustomId || m }}
                </a-tag>
              </div>
            </div>
          </a-tab-pane>
        </a-tabs>
      </div>

      <!-- 确认 (create step 3 / edit step 2) -->
      <div v-if="isConfirmStep">
        <a-descriptions :column="1" bordered size="small" title="确认信息">
          <a-descriptions-item label="供应商">{{ getSupplierName(wizardForm.supplierType) }}</a-descriptions-item>
          <a-descriptions-item label="名称">{{ wizardForm.name }}</a-descriptions-item>
          <a-descriptions-item label="Base URL">{{ wizardForm.apiAddress }}</a-descriptions-item>
          <a-descriptions-item v-if="currentPreset?.isOpenAIProtocol" label="接口路径">{{ wizardForm._supportedPathList.join(', ') }}</a-descriptions-item>
          <a-descriptions-item v-if="currentPreset?.isOpenAIProtocol" label="透传路径">{{ (wizardForm._passthroughPathList || []).join(', ') }}</a-descriptions-item>
          <a-descriptions-item v-if="currentPreset?.isOpenAIProtocol" label="降级目标">{{ wizardForm.fallbackTarget || 'Chat' }}</a-descriptions-item>
          <a-descriptions-item label="SSE">{{ wizardForm.sseEnabled ? '启用' : '禁用' }}</a-descriptions-item>
          <a-descriptions-item label="密钥数量">{{ parsedKeysCount }} 个</a-descriptions-item>
          <a-descriptions-item label="模型数量">{{ selectedPresetModels.length + manualModels.length }} 个</a-descriptions-item>
        </a-descriptions>
      </div>

      <div class="wizard-footer">
        <a-button v-if="wizardStep > 0" @click="wizardStep--">上一步</a-button>
        <!-- 新建模式 step 0：不显示"下一步"，用户需点击供应商卡片选择 -->
        <a-button
          v-if="wizardStep < wizardTotalSteps - 1 && !(wizardStep === 0 && !editingChannelId)"
          type="primary"
          @click="wizardNext"
        >
          {{ isModelStep ? '确认配置' : '下一步' }}
        </a-button>
        <a-button v-if="isConfirmStep" type="primary" :loading="wizardSaving" @click="submitWizard">
          {{ editingChannelId ? '保存修改' : '完成创建' }}
        </a-button>
      </div>
    </a-modal>

    <!-- ===== 渠道详情管理弹窗 ===== -->
    <a-modal v-model:visible="detailVisible" :title="`管理渠道: ${detailChannel?.name}`" width="900px" :footer="false">
      <a-tabs v-if="detailChannel">
        <a-tab-pane key="keys" title="API 密钥">
          <div style="margin-bottom: 12px">
            <a-space>
              <a-input v-model="newKeyValue" placeholder="输入 API Key" style="width: 400px" @keyup.enter="addSingleKey" />
              <a-button type="primary" size="small" @click="addSingleKey" :disabled="!newKeyValue">添加</a-button>
            </a-space>
            <a-divider orientation="left" style="margin: 12px 0">批量添加</a-divider>
            <a-textarea v-model="batchKeysText" placeholder="每行一个 API Key" :rows="3" />
            <a-button size="small" type="outline" @click="batchAddKeys" :disabled="!batchKeysText.trim()" style="margin-top: 8px">批量添加</a-button>
          </div>
          <a-table :columns="keyColumns" :data="detailKeys" row-key="id" size="small" :pagination="false">
            <template #action="{ record }">
              <a-button size="mini" type="text" status="danger" @click="deleteKey(record.id)">删除</a-button>
            </template>
          </a-table>
        </a-tab-pane>

        <a-tab-pane key="models" title="模型映射">
          <div style="margin-bottom: 12px">
            <a-row :gutter="8" align="center">
              <a-col :span="5">
                <a-input v-model="newModelForm.originalModelId" placeholder="原始模型ID" size="small" />
              </a-col>
              <a-col :span="5">
                <a-input v-model="newModelForm.modelName" placeholder="显示名称" size="small" />
              </a-col>
              <a-col :span="5">
                <a-input v-model="newModelForm.customModelId" placeholder="自定义模型ID" size="small" />
              </a-col>
              <a-col :span="3">
                <a-input-number v-model="newModelForm.weight" :min="1" :max="100" placeholder="权重" size="small" style="width:100%" />
              </a-col>
              <a-col :span="3">
                <a-button type="primary" size="small" @click="addSingleModel" long>添加</a-button>
              </a-col>
            </a-row>
          </div>
          <a-table :columns="modelColumns" :data="detailModels" row-key="id" size="small" :pagination="false">
            <template #action="{ record }">
              <a-button size="mini" type="text" status="danger" @click="deleteModel(record.id)">删除</a-button>
            </template>
          </a-table>
        </a-tab-pane>

        <a-tab-pane key="test" title="连通性测试">
          <!-- 模型选择下拉 + 测试消息 -->
          <div style="margin-bottom: 16px">
            <a-row :gutter="12" align="center">
              <a-col :span="6">
                <a-select v-model="testModelId" placeholder="选择模型" size="small" allow-search>
                  <a-option v-for="m in detailModels" :key="m.id" :value="m.originalModelId">
                    {{ m.originalModelId }}
                    <span v-if="m.customModelId" style="color: var(--color-text-3); font-size: 11px">→ {{ m.customModelId }}</span>
                  </a-option>
                </a-select>
              </a-col>
              <a-col :span="12">
                <a-input v-model="testMessage" placeholder="测试消息 (默认 Hello)" size="small" />
              </a-col>
              <a-col :span="6">
                <a-button type="primary" size="small" :loading="testing" @click="runSingleTest" long>测试此渠道</a-button>
              </a-col>
            </a-row>
          </div>

          <!-- 快速测试：列出所有模型，每个都可以测试 -->
          <div v-if="detailModels.length > 0" style="margin-bottom: 12px">
            <div class="batch-header">
              <span>模型列表（点击测试）</span>
            </div>
            <div class="model-test-list">
              <div v-for="m in detailModels" :key="m.id" class="model-test-row">
                <div class="model-test-info">
                  <code class="model-id">{{ m.originalModelId }}</code>
                  <span class="model-arrow">→</span>
                  <code class="model-custom">{{ m.customModelId || m.originalModelId }}</code>
                  <span v-if="m.modelName" class="model-name">({{ m.modelName }})</span>
                </div>
                <a-button
                  size="mini"
                  type="outline"
                  :loading="testing && testModelId === m.originalModelId"
                  @click="testModelId = m.originalModelId; runSingleTest()"
                >
                  测试
                </a-button>
              </div>
            </div>
          </div>

          <!-- 测试结果 -->
          <div v-if="testResult" class="test-result" :class="testResult.success ? 'success' : 'fail'">
            <div class="test-result-header">
              <a-tag :color="testResult.success ? 'green' : 'red'">{{ testResult.success ? '连接成功' : '连接失败' }}</a-tag>
              <span class="test-model">模型: {{ testResult.modelId }}</span>
              <span class="test-latency">延迟: {{ testResult.latencyMs }}ms</span>
              <span class="test-status">HTTP {{ testResult.statusCode }}</span>
            </div>
            <pre class="test-result-body">{{ testResult.responseBody || testResult.error }}</pre>
          </div>
        </a-tab-pane>
      </a-tabs>
    </a-modal>

    <!-- ===== 模型测试面板（全局） ===== -->
    <a-card v-if="showTestPanel" :bordered="false" class="table-card" title="模型测试">
      <template #extra>
        <a-button size="small" type="primary" :loading="testing" @click="runGlobalTest">开始测试</a-button>
      </template>
      <a-row :gutter="16" style="margin-bottom: 16px">
        <a-col :span="6">
          <a-input v-model="testModelId" placeholder="模型ID (默认 gpt-4o-mini)" size="small" />
        </a-col>
        <a-col :span="12">
          <a-input v-model="testMessage" placeholder="测试消息" size="small" />
        </a-col>
        <a-col :span="6">
          <a-select v-model="testChannelIds" placeholder="选择渠道(空=全部)" multiple size="small" allow-clear>
            <a-option v-for="ch in channels" :key="ch.id" :value="ch.id">{{ ch.name }}</a-option>
          </a-select>
        </a-col>
      </a-row>
      <a-table v-if="testResults.length > 0" :columns="testColumns" :data="testResults" row-key="channelId" size="small" :pagination="false">
        <template #success="{ record }">
          <a-tag :color="record.success ? 'green' : 'red'" size="small">{{ record.success ? '成功' : '失败' }}</a-tag>
        </template>
      </a-table>
    </a-card>
  </div>
</template>

<script setup>
import { ref, onMounted, reactive, computed } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconPlus, IconExperiment } from '@arco-design/web-vue/es/icon'
import { channelApi } from '../../api'

// ===== 数据 =====
const channels = ref([])
const loading = ref(false)
const supplierPresets = ref([])

const columns = [
  { title: 'ID', dataIndex: 'id', width: 50 },
  { title: '渠道名称', dataIndex: 'name', width: 180 },
  { title: '供应商', slotName: 'supplierType', width: 100 },
  { title: '协议', slotName: 'protocolType', width: 80 },
  { title: '流式', slotName: 'sseEnabled', width: 70 },
  { title: '模型数', slotName: 'modelCount', width: 100 },
  { title: '状态', slotName: 'enabled', width: 70 },
  { title: '操作', slotName: 'action', width: 220, fixed: 'right' }
]

function getSupplierColor(type) {
  const map = { OpenAI: '#10a37f', Azure: '#0078d4', Anthropic: '#d97757', Google: '#4285f4', DeepSeek: '#4d6bfe', Groq: '#f97316', Together: '#0f9', Custom: 'gray' }
  return map[type] || 'gray'
}
function getSupplierName(type) {
  const map = { OpenAI: 'OpenAI', Azure: 'Azure', Anthropic: 'Claude', Google: 'Gemini', DeepSeek: 'DeepSeek', Groq: 'Groq', Together: 'Together', Custom: '自定义' }
  return map[type] || type
}
function getPreset(type) {
  return supplierPresets.value.find(p => p.type === type)
}

const currentPreset = computed(() => getPreset(wizardForm.supplierType))

// 编辑/新建的步骤列表（含状态）
const wizardSteps = computed(() => {
  const step = wizardStep.value
  const steps = editingChannelId.value
    ? [
        { title: '配置参数' },
        { title: '添加模型' },
        { title: '确认' }
      ]
    : [
        { title: '选择供应商' },
        { title: '配置参数' },
        { title: '添加模型' },
        { title: '确认' }
      ]
  return steps.map((s, i) => {
    if (i < step) s.status = 'finish'
    else if (i === step) s.status = 'process'
    else s.status = 'wait'
    return s
  })
})

// 步骤总数（根据模式动态变化）
const wizardTotalSteps = computed(() => editingChannelId.value ? 3 : 4)

// 当前是否为配置参数步骤
const isConfigStep = computed(() => {
  if (editingChannelId.value) return wizardStep.value === 0
  return wizardStep.value === 1
})

// 当前是否为添加模型步骤
const isModelStep = computed(() => {
  if (editingChannelId.value) return wizardStep.value === 1
  return wizardStep.value === 2
})

// 当前是否为确认步骤
const isConfirmStep = computed(() => {
  if (editingChannelId.value) return wizardStep.value === 2
  return wizardStep.value === 3
})

function getApiPlaceholder() {
  const preset = currentPreset.value
  if (preset?.defaultApi) return `如: ${preset.defaultApi}`
  return 'https://your-api.example.com/v1'
}
function getApiHint() {
  const type = wizardForm.supplierType
  const base = '实际请求时，系统会在 Base URL 后自动拼接接口路径。'
  switch (type) {
    case 'OpenAI': return `如填写 https://api.openai.com/v1，系统自动追加 /chat/completions 或 /responses`
    case 'Azure': return `如填写 https://my-resource.openai.azure.com，系统自动追加 /openai/deployments/{model}/chat/completions`
    case 'Anthropic': return `如填写 https://api.anthropic.com，系统自动追加 /v1/messages`
    case 'Google': return `如填写 https://generativelanguage.googleapis.com/v1beta，系统自动追加 /models/{model}:generateContent`
    case 'DeepSeek': return `如填写 https://api.deepseek.com/v1，系统自动追加 /chat/completions`
    case 'Groq': return `如填写 https://api.groq.com/openai/v1，系统自动追加 /chat/completions`
    case 'Together': return `如填写 https://api.together.xyz/v1，系统自动追加 /chat/completions`
    case 'Custom': return 'OpenAI 兼容接口，系统自动追加 /chat/completions、/responses 或 /messages'
    default: return '系统将自动拼接正确的接口路径'
  }
}

// ===== 加载 =====
async function loadAll() {
  loading.value = true
  try {
    const [chRes, presetRes] = await Promise.all([channelApi.list(), channelApi.getSupplierPresets()])
    if (chRes.code === 200) {
      const list = chRes.data || []
      for (const ch of list) {
        try {
          const detailRes = await channelApi.getModels(ch.id)
          ch._modelCount = detailRes.code === 200 ? (detailRes.data?.length || 0) : 0
        } catch { ch._modelCount = 0 }
      }
      channels.value = list
    }
    if (presetRes.code === 200) supplierPresets.value = presetRes.data || []
  } finally { loading.value = false }
}

async function toggleChannel(record, val) {
  try {
    await channelApi.update(record.id, { ...record, enabled: val })
    record.enabled = val
    Message.success(val ? '已启用' : '已禁用')
  } catch { /* ignore */ }
}

// ===== 创建/编辑向导 =====
const wizardVisible = ref(false)
const wizardStep = ref(0)
const wizardSaving = ref(false)
const editingChannelId = ref(null)
const wizardForm = reactive({
  name: '', remark: '', supplierType: 'OpenAI', apiAddress: '',
  timeoutSeconds: 30, cooldownSeconds: 60, protocolType: 'Chat', fallbackTarget: 'Chat', sseEnabled: true,
  _passthroughPathList: ['chat','responses','messages'],
  apiKeys: [], _availableModels: [], _supportsResponses: true, _supportedPathList: ['chat', 'responses', 'messages']
})
const keysTextarea = ref('')
const modelBatchCustomId = ref('')
const selectedPresetModels = ref([])
const manualModelInput = ref('')
const manualModels = ref([])
const existingKeys = ref([])
const existingModels = ref([])

const parsedKeysCount = computed(() => {
  return keysTextarea.value.split('\n').map(k => k.trim()).filter(Boolean).length
})

function showCreateWizard() {
  editingChannelId.value = null
  wizardStep.value = 0
  wizardVisible.value = true
  resetWizardForm()
}

function resetWizardForm() {
  Object.assign(wizardForm, {
    name: '', remark: '', supplierType: 'OpenAI', apiAddress: '',
    timeoutSeconds: 30, cooldownSeconds: 60, protocolType: 'Chat', sseEnabled: true,
    apiKeys: [], _availableModels: [], _supportsResponses: true, _supportedPathList: ['chat', 'responses', 'messages']
  })
  keysTextarea.value = ''
  modelBatchCustomId.value = ''
  selectedPresetModels.value = []
  manualModels.value = []
  manualModelInput.value = ''
  existingKeys.value = []
  existingModels.value = []
}

function selectSupplier(preset) {
  wizardForm.supplierType = preset.type
  wizardForm.apiAddress = preset.defaultApi
  wizardForm._availableModels = preset.defaultModels || []
  wizardForm._supportedPathList = ['chat', 'responses', 'messages']
  wizardForm._passthroughPathList = ['chat', 'responses', 'messages']
  wizardForm._supportsResponses = true
  wizardForm._supportsMessages = true
  if (!preset.isOpenAIProtocol) {
    wizardForm.protocolType = 'Chat'
    wizardForm._supportedPathList = ['chat', 'responses', 'messages']
  }
  wizardStep.value = 1  // 新建模式下进入"配置参数"
}

function wizardNext() {
  // 新建模式 step 0: 选择供应商（需要先点击供应商卡片）
  if (wizardStep.value === 0 && !editingChannelId.value) {
    Message.warning('请先选择供应商'); return
  }
  // 配置参数步骤验证
  if (isConfigStep.value) {
    if (!wizardForm.name.trim()) { Message.warning('请填写渠道名称'); return }
    if (!wizardForm.apiAddress.trim()) { Message.warning('请填写 API 地址'); return }
    if (currentPreset.value?.isOpenAIProtocol && wizardForm._supportedPathList.length === 0) {
      Message.warning('请至少选择一个接口路径'); return
    }
    wizardStep.value++
    return
  }
  // 添加模型步骤
  if (isModelStep.value) { wizardStep.value++; return }
}

function addManualModel() {
  const val = manualModelInput.value.trim()
  if (!val) return
  if (!manualModels.value.includes(val)) {
    manualModels.value.push(val)
  }
  manualModelInput.value = ''
}

async function submitWizard() {
  wizardSaving.value = true
  try {
    const supportedPaths = currentPreset.value?.isOpenAIProtocol
      ? wizardForm._supportedPathList.join(',')
      : (currentPreset.value?.supportedPaths || []).join(',') || 'chat'

    const allModels = [
      ...selectedPresetModels.value.map(m => ({
        originalModelId: m, modelName: m, customModelId: modelBatchCustomId.value || m, weight: 1
      })),
      ...manualModels.value.map(m => ({
        originalModelId: m, modelName: m, customModelId: modelBatchCustomId.value || m, weight: 1
      }))
    ]

    const apiKeys = keysTextarea.value.split('\n').map(k => k.trim()).filter(Boolean)

    if (editingChannelId.value) {
      const existingChannel = channels.value.find(ch => ch.id === editingChannelId.value);
      await channelApi.update(editingChannelId.value, {
        id: editingChannelId.value,
        name: wizardForm.name, remark: wizardForm.remark,
        supplierType: wizardForm.supplierType, apiAddress: wizardForm.apiAddress,
        timeoutSeconds: wizardForm.timeoutSeconds, cooldownSeconds: wizardForm.cooldownSeconds,
        protocolType: wizardForm.protocolType, sseEnabled: wizardForm.sseEnabled,
        supportedPaths, passthroughPaths: (wizardForm._passthroughPathList || []).join(','),
        fallbackTarget: wizardForm.fallbackTarget, enabled: existingChannel?.enabled ?? true
      })
      // 编辑时也保存密钥（如果有新输入的）
      if (apiKeys.length > 0) {
        await channelApi.batchAddKeys(editingChannelId.value, apiKeys)
      }
      // 编辑时也保存模型（如果有新添加的）
      if (allModels.length > 0) {
        await channelApi.batchAddModels(editingChannelId.value, allModels)
      }
      Message.success('渠道更新成功')
    } else {
      const payload = {
        name: wizardForm.name, remark: wizardForm.remark,
        supplierType: wizardForm.supplierType, apiAddress: wizardForm.apiAddress,
        timeoutSeconds: wizardForm.timeoutSeconds, cooldownSeconds: wizardForm.cooldownSeconds,
        protocolType: wizardForm.protocolType, supportedPaths,
        passthroughPaths: (wizardForm._passthroughPathList || []).join(','),
        fallbackTarget: wizardForm.fallbackTarget,
        sseEnabled: wizardForm.sseEnabled, apiKeys, models: allModels
      }
      const res = await channelApi.create(payload)
      if (res.code === 200) Message.success('渠道创建成功！')
    }

    wizardVisible.value = false
    editingChannelId.value = null
    await loadAll()
  } catch (e) { console.error(e) } finally { wizardSaving.value = false }
}

// ===== 编辑渠道 =====
async function editChannel(record) {
  editingChannelId.value = record.id
  wizardVisible.value = true
  wizardStep.value = 0  // 编辑模式从 step 0 开始，step 0 即"配置参数"
  const preset = supplierPresets.value.find(p => p.type === record.supplierType)
  Object.assign(wizardForm, {
    name: record.name, remark: record.remark || '', supplierType: record.supplierType,
    apiAddress: record.apiAddress, timeoutSeconds: record.timeoutSeconds,
    cooldownSeconds: record.cooldownSeconds, protocolType: record.protocolType,
    sseEnabled: record.sseEnabled, _availableModels: preset?.defaultModels || [],
    _supportedPathList: (record.supportedPaths || 'chat').split(',').filter(Boolean),
    _passthroughPathList: (record.passthroughPaths || record.supportedPaths || 'chat').split(',').filter(Boolean),
    _supportsResponses: (record.supportedPaths || '').includes('responses'),
    _supportsMessages: (record.supportedPaths || '').includes('messages'),
    fallbackTarget: record.fallbackTarget || record.protocolType || 'Chat',
    apiKeys: []
  })
  keysTextarea.value = ''
  selectedPresetModels.value = []
  manualModels.value = []
  manualModelInput.value = ''

  // 加载已有密钥和模型
  try {
    const [keysRes, modelsRes] = await Promise.all([
      channelApi.getKeys(record.id),
      channelApi.getModels(record.id)
    ])
    existingKeys.value = keysRes.code === 200 ? (keysRes.data || []) : []
    existingModels.value = modelsRes.code === 200 ? (modelsRes.data || []) : []
  } catch {
    existingKeys.value = []
    existingModels.value = []
  }
}

async function removeExistingKey(keyId) {
  try {
    await channelApi.deleteKey(editingChannelId.value, keyId)
    existingKeys.value = existingKeys.value.filter(k => k.id !== keyId)
    Message.success('密钥已删除')
  } catch { /* ignore */ }
}

async function removeExistingModel(modelId) {
  try {
    await channelApi.deleteModel(editingChannelId.value, modelId)
    existingModels.value = existingModels.value.filter(m => m.id !== modelId)
    Message.success('模型已删除')
  } catch { /* ignore */ }
}

async function deleteChannel(id) {
  await channelApi.delete(id)
  Message.success('删除成功')
  await loadAll()
}

// ===== 渠道详情管理 =====
const detailVisible = ref(false)
const detailChannel = ref(null)
const detailKeys = ref([])
const detailModels = ref([])
const newKeyValue = ref('')
const batchKeysText = ref('')
const newModelForm = reactive({ originalModelId: '', modelName: '', customModelId: '', weight: 1 })

const keyColumns = [
  { title: 'ID', dataIndex: 'id', width: 50 },
  { title: '密钥', dataIndex: 'keyValue', ellipsis: true, width: 300 },
  { title: '权重', dataIndex: 'weight', width: 60 },
  { title: '状态', dataIndex: 'status', width: 70 },
  { title: '操作', slotName: 'action', width: 80 }
]
const modelColumns = [
  { title: '原始模型ID', dataIndex: 'originalModelId', width: 150 },
  { title: '显示名称', dataIndex: 'modelName', width: 120 },
  { title: '自定义模型ID', dataIndex: 'customModelId', width: 150 },
  { title: '权重', dataIndex: 'weight', width: 60 },
  { title: '操作', slotName: 'action', width: 80 }
]

async function manageChannelDetail(record) {
  detailChannel.value = record
  detailVisible.value = true
  newKeyValue.value = ''
  batchKeysText.value = ''
  Object.assign(newModelForm, { originalModelId: '', modelName: '', customModelId: '', weight: 1 })
  await Promise.all([loadKeys(), loadModels()])
}

async function loadKeys() {
  if (!detailChannel.value) return
  const res = await channelApi.getKeys(detailChannel.value.id)
  if (res.code === 200) detailKeys.value = res.data
}
async function loadModels() {
  if (!detailChannel.value) return
  const res = await channelApi.getModels(detailChannel.value.id)
  if (res.code === 200) detailModels.value = res.data
}

async function addSingleKey() {
  if (!newKeyValue.value.trim()) return
  await channelApi.addKey(detailChannel.value.id, { keyValue: newKeyValue.value.trim(), weight: 1, status: 1 })
  Message.success('添加成功')
  newKeyValue.value = ''
  await loadKeys()
}
async function batchAddKeys() {
  const keys = batchKeysText.value.split('\n').map(k => k.trim()).filter(Boolean)
  if (keys.length === 0) return
  await channelApi.batchAddKeys(detailChannel.value.id, keys)
  Message.success(`成功添加 ${keys.length} 个密钥`)
  batchKeysText.value = ''
  await loadKeys()
}
async function addSingleModel() {
  if (!newModelForm.originalModelId || !newModelForm.customModelId) {
    Message.warning('请填写原始模型ID和自定义模型ID'); return
  }
  await channelApi.addModel(detailChannel.value.id, { ...newModelForm })
  Message.success('模型添加成功')
  Object.assign(newModelForm, { originalModelId: '', modelName: '', customModelId: '', weight: 1 })
  await loadModels()
}
async function deleteModel(modelId) {
  await channelApi.deleteModel(detailChannel.value.id, modelId)
  Message.success('删除成功')
  await loadModels()
}
async function deleteKey(keyId) {
  await channelApi.deleteKey(detailChannel.value.id, keyId)
  Message.success('密钥已删除')
  await loadKeys()
}

// ===== 模型测试 =====
const showTestPanel = ref(false)
const testing = ref(false)
const testModelId = ref('')
const testMessage = ref('Hello, respond with just OK.')
const testChannelIds = ref([])
const testResults = ref([])
const testResult = ref(null)

const testColumns = [
  { title: '渠道', dataIndex: 'channelName', width: 150 },
  { title: '模型', dataIndex: 'modelId', width: 150 },
  { title: '状态', slotName: 'success', width: 80 },
  { title: 'HTTP', dataIndex: 'statusCode', width: 70 },
  { title: '延迟', dataIndex: 'latencyMs', width: 90 },
  { title: '响应', dataIndex: 'responseBody', ellipsis: true }
]

async function runGlobalTest() {
  testing.value = true
  testResults.value = []
  try {
    const channelIds = testChannelIds.value.length > 0
      ? testChannelIds.value
      : channels.value.filter(c => c.enabled).map(c => c.id)
    const res = await channelApi.testModel({
      channelIds,
      modelId: testModelId.value || undefined,
      testMessage: testMessage.value || undefined
    })
    if (res.code === 200) testResults.value = res.data.results || []
  } catch (e) { console.error(e) } finally { testing.value = false }
}

async function runSingleTest() {
  if (!detailChannel.value) return
  testing.value = true
  testResult.value = null
  try {
    const res = await channelApi.testModel({
      channelIds: [detailChannel.value.id],
      modelId: testModelId.value || undefined,
      testMessage: testMessage.value || undefined
    })
    if (res.code === 200 && res.data.results?.length > 0) {
      testResult.value = res.data.results[0]
    }
  } catch (e) { console.error(e) } finally { testing.value = false }
}

onMounted(loadAll)
</script>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 16px;
  flex-wrap: wrap;
  gap: 12px;
}
.header-left { display: flex; align-items: center; }
.page-header h2 { margin: 0; white-space: nowrap; }
.table-card { margin-bottom: 16px; }

/* 供应商选择网格 */
.supplier-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 12px;
}
.supplier-card {
  border: 2px solid var(--color-border-2);
  border-radius: 8px;
  padding: 20px 12px;
  text-align: center;
  cursor: pointer;
  transition: all 0.2s;
}
.supplier-card:hover { border-color: var(--color-primary-light-2); background: var(--color-fill-1); }
.supplier-card.active { border-color: rgb(var(--primary-6)); background: rgb(var(--primary-1)); }
.supplier-icon {
  width: 40px; height: 40px; border-radius: 8px;
  color: #fff; display: flex; align-items: center; justify-content: center;
  font-size: 18px; font-weight: 700; margin: 0 auto 8px;
}
.supplier-name { font-size: 13px; font-weight: 500; margin-bottom: 6px; }
.supplier-paths { display: flex; gap: 4px; justify-content: center; }

.wizard-footer {
  display: flex; justify-content: flex-end; gap: 8px;
  margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--color-border-2);
}

.form-hint { font-size: 12px; color: var(--color-text-3); margin-top: 2px; }

/* 批量区域 */
.batch-section { padding: 4px 0; }
.batch-header {
  display: flex; justify-content: space-between; align-items: center;
  margin-bottom: 8px; font-size: 13px; color: var(--color-text-2);
}
.batch-count { margin-top: 6px; font-size: 12px; color: var(--color-text-3); }

.model-section-title {
  font-size: 12px; color: var(--color-text-3); font-weight: 500;
  margin-bottom: 6px; margin-top: 4px;
}
.model-preset-list { max-height: 220px; overflow-y: auto; }
.model-preset-item { display: inline-flex; align-items: center; gap: 6px; }
.model-preset-item .model-id {
  font-size: 12px; color: var(--color-text-2);
  background: var(--color-fill-2); padding: 1px 4px; border-radius: 3px;
}
.model-preset-item .model-arrow { color: var(--color-text-3); }
.model-preset-item .model-custom {
  font-size: 12px; color: rgb(var(--primary-6)); font-weight: 500;
  background: rgb(var(--primary-1)); padding: 1px 4px; border-radius: 3px;
}

.manual-model-input { display: flex; gap: 8px; }
.manual-model-tags { margin-top: 8px; display: flex; flex-wrap: wrap; gap: 6px; }

/* 已有密钥/模型列表 */
.existing-list {
  display: flex; flex-direction: column; gap: 6px;
  max-height: 200px; overflow-y: auto;
  padding: 8px; background: var(--color-fill-1); border-radius: 6px;
}
.existing-item {
  display: flex; align-items: center; justify-content: space-between;
  padding: 4px 8px; background: var(--color-bg-2); border-radius: 4px;
}
.existing-item .key-text {
  font-size: 12px; color: var(--color-text-2);
  background: var(--color-fill-2); padding: 2px 6px; border-radius: 3px;
  word-break: break-all;
}
.existing-model-info {
  display: flex; align-items: center; gap: 6px;
  font-size: 12px;
}
.existing-model-info .model-name {
  color: var(--color-text-3); font-size: 11px;
}

/* 模型测试列表 */
.model-test-list {
  display: flex; flex-direction: column; gap: 6px;
  max-height: 240px; overflow-y: auto;
  padding: 8px; background: var(--color-fill-1); border-radius: 6px;
}
.model-test-row {
  display: flex; align-items: center; justify-content: space-between;
  padding: 6px 8px; background: var(--color-bg-2); border-radius: 4px;
}
.model-test-info {
  display: flex; align-items: center; gap: 6px;
  font-size: 12px;
}

/* 测试结果 */
.test-result {
  padding: 16px; border-radius: 8px; margin-top: 12px;
}
.test-result.success { background: var(--color-success-light-1); border: 1px solid var(--color-success-light-2); }
.test-result.fail { background: var(--color-danger-light-1); border: 1px solid var(--color-danger-light-2); }
.test-result-header { display: flex; align-items: center; gap: 12px; margin-bottom: 8px; flex-wrap: wrap; }
.test-model { color: rgb(var(--primary-6)); font-size: 13px; font-weight: 500; }
.test-latency { color: var(--color-text-3); font-size: 13px; }
.test-status { color: var(--color-text-3); font-size: 13px; }
.test-result-body {
  font-size: 12px; max-height: 400px; overflow-y: auto;
  background: rgba(0,0,0,0.04); padding: 8px; border-radius: 4px;
  white-space: pre-wrap; word-break: break-all; margin: 0;
}
</style>
