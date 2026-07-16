<template>
  <div class="chains-page">
    <div class="page-header">
      <div>
        <h2>模型链配置</h2>
        <span class="page-desc">将自定义模型 ID 映射到多个渠道的多个模型，按权重负载均衡、按优先级降级</span>
      </div>
      <a-button type="primary" size="large" @click="showCreateWizard">
        <template #icon><icon-plus /></template>
        新建模型链
      </a-button>
    </div>

    <!-- 模型链列表 -->
    <div v-if="chains.length === 0" class="empty-state">
      <div class="empty-icon">🔗</div>
      <div class="empty-title">暂无模型链配置</div>
      <div class="empty-desc">点击「新建模型链」创建第一个模型链，将自定义模型 ID 映射到多个渠道</div>
    </div>

    <div v-for="chain in chains" :key="chain.customModelId" class="chain-card">
      <div class="chain-card-header">
        <div class="chain-info">
          <div class="chain-title">
            <span class="chain-id">{{ chain.customModelId }}</span>
            <a-tag v-if="chain.displayName && chain.displayName !== chain.customModelId" color="arcoblue" size="small">
              {{ chain.displayName }}
            </a-tag>
            <a-tag size="small" :color="getChainTypeColor(chain)">{{ getChainTypeLabel(chain) }}</a-tag>
            <a-tag size="small" color="arcoblue">{{ chain.nodes.length }} 个节点</a-tag>
            <a-tag size="small" :color="getEnabledCount(chain) > 0 ? 'green' : 'red'">
              {{ getEnabledCount(chain) }} 个启用
            </a-tag>
          </div>
          <div class="chain-routing-hint">
            优先级 {{ getPrioritySummary(chain) }} · 总权重 {{ getTotalWeight(chain) }}
          </div>
        </div>
        <a-space>
          <a-button size="small" type="outline" @click="addNodeToChain(chain)">
            <template #icon><icon-plus /></template>
            添加节点
          </a-button>
          <a-popconfirm content="确认删除整个模型链?" @ok="deleteChain(chain.customModelId)">
            <a-button size="small" type="text" status="danger">删除整链</a-button>
          </a-popconfirm>
        </a-space>
      </div>

      <a-table
        :columns="nodeColumns"
        :data="chain.nodes"
        row-key="id"
        size="small"
        :pagination="false"
        :stripe="true"
      >
        <template #channelName="{ record }">
          <a-space>
            <span :class="['supplier-dot', record._supplierType]"></span>
            {{ record.channelName }}
          </a-space>
        </template>
        <template #weight="{ record }">
          <div class="weight-bar">
            <div class="weight-fill" :style="{ width: getWeightPercent(chain, record) + '%' }"></div>
            <span class="weight-text">{{ record.weight }}</span>
          </div>
        </template>
        <template #priority="{ record }">
          <a-tag :color="record.priority === 0 ? 'green' : record.priority === 1 ? 'arcoblue' : 'gray'" size="small">
            P{{ record.priority }}
          </a-tag>
        </template>
        <template #enabled="{ record }">
          <a-switch :model-value="record.enabled" size="mini" @change="(v) => toggleNode(record, v)" />
        </template>
        <template #action="{ record }">
          <a-space>
            <a-button size="mini" type="text" @click="editNode(record)">编辑</a-button>
            <a-popconfirm content="确认删除此节点?" @ok="deleteNode(record.id)">
              <a-button size="mini" type="text" status="danger">删除</a-button>
            </a-popconfirm>
          </a-space>
        </template>
      </a-table>
    </div>

    <!-- 新建模型链向导 -->
    <a-modal v-model:visible="wizardVisible" title="新建模型链" width="900px" :footer="false" :mask-closable="false">
      <a-steps :current="wizardStep" size="small" style="margin-bottom: 24px">
        <a-step v-for="(s, i) in chainWizardSteps" :key="i" :title="s.title" :status="s.status" />
      </a-steps>

      <!-- Step 1: 基本信息 -->
      <div v-if="wizardStep === 0">
        <a-form :model="wizardForm" layout="vertical">
          <a-form-item label="自定义模型 ID (对外暴露)" required>
            <a-input
              v-model="wizardForm.customModelId"
              placeholder="如: my-gpt4-pool、production-claude"
              size="large"
            />
            <div class="form-hint">
              这是用户调用时传入的 model 参数值，将作为负载均衡池的标识
            </div>
          </a-form-item>
          <a-form-item label="显示名称">
            <a-input v-model="wizardForm.displayName" placeholder="如: GPT-4 多源负载池" />
          </a-form-item>
          <a-form-item label="链类型" required>
            <a-radio-group v-model="wizardForm.chainType" @change="onChainTypeChange">
              <a-radio value="Text">文本 LLM（/v1/chat/completions）</a-radio>
              <a-radio value="Image">图片转发（/v1/images/generations）</a-radio>
            </a-radio-group>
            <div class="form-hint">文本链只参与 LLM 转发，图片链只参与图片转发，同 ID 可同时建两条互不串味。切换链类型会清空已选的不匹配渠道节点</div>
          </a-form-item>
          <a-form-item label="描述">
            <a-textarea v-model="wizardForm.description" placeholder="可选: 链用途说明" :rows="2" />
          </a-form-item>
        </a-form>
      </div>

      <!-- Step 2: 选择节点（多渠道 + 多模型） -->
      <div v-if="wizardStep === 1">
        <div class="nodes-section">
          <div class="nodes-header">
            <span>已选节点 ({{ wizardNodes.length }})</span>
            <a-button size="small" type="primary" @click="addNodeRow">
              <template #icon><icon-plus /></template>
              添加节点
            </a-button>
          </div>

          <div v-if="wizardNodes.length === 0" class="nodes-empty">
            请点击「添加节点」来选择渠道和模型
          </div>

          <div v-for="(node, idx) in wizardNodes" :key="idx" class="node-row">
            <div class="node-row-number">#{{ idx + 1 }}</div>
            <div class="node-row-fields">
              <a-row :gutter="8" align="center" style="margin-bottom: 8px">
                <a-col :span="11">
                  <a-select v-model="node.channelId" placeholder="选择渠道" size="small" @change="(v) => onNodeChannelChange(idx, v)" style="width:100%">
                    <a-option v-for="ch in getChannelsForChainType(wizardForm.chainType)" :key="ch.id" :value="ch.id">
                      {{ ch.name }} <span style="color:var(--color-text-3);font-size:11px">{{ getSupplierName(ch.supplierType) }}</span>
                    </a-option>
                  </a-select>
                </a-col>
                <a-col :span="11">
                  <a-select
                    v-model="node.originalModelId"
                    placeholder="选择模型"
                    size="small"
                    allow-search
                    allow-create
                    style="width:100%"
                  >
                    <a-option v-for="m in getChannelModels(node.channelId)" :key="m" :value="m">{{ m }}</a-option>
                  </a-select>
                </a-col>
                <a-col :span="2">
                  <a-button size="mini" type="text" status="danger" @click="removeNodeRow(idx)">
                    <template #icon><icon-delete /></template>
                  </a-button>
                </a-col>
              </a-row>
              <a-row :gutter="8" align="center">
                <a-col :span="8">
                  <div class="weight-input-group">
                    <span class="weight-label">权重</span>
                    <a-input-number v-model="node.weight" :min="1" :max="100" size="small" style="width:100%" />
                  </div>
                </a-col>
                <a-col :span="8">
                  <div class="weight-input-group">
                    <span class="weight-label">优先级</span>
                    <a-input-number v-model="node.priority" :min="0" :max="100" size="small" style="width:100%" />
                  </div>
                </a-col>
                <a-col :span="8">
                  <div class="weight-input-group">
                    <span class="weight-label">启用</span>
                    <a-switch v-model="node.enabled" size="small" />
                  </div>
                </a-col>
              </a-row>
            </div>
          </div>

          <div class="nodes-preview" v-if="wizardNodes.length > 0">
            <div class="preview-title">路由预览</div>
            <div class="preview-flow">
              <template v-for="(group, gIdx) in groupedNodes" :key="gIdx">
                <div v-if="gIdx > 0" class="flow-arrow">↓ 降级</div>
                <div class="priority-group">
                  <div class="priority-label">优先级 {{ group.priority }}</div>
                  <div class="priority-nodes">
                    <span v-for="(n, nIdx) in group.nodes" :key="nIdx" class="priority-node">
                      {{ getChannelName(n.channelId) }}:{{ n.originalModelId || '?' }}
                      <span class="node-weight">(权重 {{ n.weight }})</span>
                    </span>
                  </div>
                </div>
              </template>
            </div>
          </div>
        </div>
      </div>

      <!-- Step 3: 确认 -->
      <div v-if="wizardStep === 2">
        <a-descriptions :column="1" bordered size="small" title="确认信息">
          <a-descriptions-item label="模型 ID">{{ wizardForm.customModelId }}</a-descriptions-item>
          <a-descriptions-item label="显示名称">{{ wizardForm.displayName || '-' }}</a-descriptions-item>
          <a-descriptions-item label="节点数">{{ wizardNodes.length }} 个</a-descriptions-item>
          <a-descriptions-item label="优先级范围">P{{ getMinPriority() }} ~ P{{ getMaxPriority() }}</a-descriptions-item>
        </a-descriptions>

        <div class="confirm-nodes">
          <div v-for="(node, idx) in wizardNodes" :key="idx" class="confirm-node">
            <span class="confirm-idx">{{ idx + 1 }}.</span>
            <span>{{ getChannelName(node.channelId) }}</span>
            <span class="confirm-arrow">→</span>
            <a-tag size="small">{{ node.originalModelId || '?' }}</a-tag>
            <span class="confirm-meta">权重 {{ node.weight }} · P{{ node.priority }}</span>
          </div>
        </div>
      </div>

      <div class="wizard-footer">
        <a-button v-if="wizardStep > 0" @click="wizardStep--">上一步</a-button>
        <a-button v-if="wizardStep < 2" type="primary" @click="wizardNext">
          {{ wizardStep === 1 ? '确认配置' : '下一步' }}
        </a-button>
        <a-button v-if="wizardStep === 2" type="primary" :loading="wizardSaving" @click="submitChain">
          完成创建
        </a-button>
      </div>
    </a-modal>

    <!-- 添加节点到已有链的弹窗 -->
    <a-modal v-model:visible="addNodeVisible" title="添加节点" width="560px" @ok="submitAddNode">
      <a-form :model="addNodeForm" layout="vertical">
        <a-form-item label="选择渠道" required>
          <a-select v-model="addNodeForm.channelId" placeholder="选择渠道" @change="onAddNodeChannelChange">
            <a-option v-for="ch in getChannelsForChainType(addNodeChainType)" :key="ch.id" :value="ch.id">
              {{ ch.name }} ({{ getSupplierName(ch.supplierType) }})
            </a-option>
          </a-select>
          <div class="form-hint">仅展示「{{ addNodeChainType === 'Image' ? '图片' : '文本 LLM' }}」渠道，链类型不可混选</div>
        </a-form-item>
        <a-form-item label="原始模型 ID" required>
          <a-select
            v-model="addNodeForm.originalModelId"
            placeholder="选择模型"
            allow-search
            allow-create
          >
            <a-option v-for="m in getChannelModels(addNodeForm.channelId)" :key="m" :value="m">{{ m }}</a-option>
          </a-select>
        </a-form-item>
        <a-row :gutter="16">
          <a-col :span="8">
            <a-form-item label="权重">
              <a-input-number v-model="addNodeForm.weight" :min="1" :max="100" style="width:100%" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="优先级">
              <a-input-number v-model="addNodeForm.priority" :min="0" :max="100" style="width:100%" />
              <div class="form-hint">越小越优先</div>
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="启用">
              <a-switch v-model="addNodeForm.enabled" />
            </a-form-item>
          </a-col>
        </a-row>
      </a-form>
    </a-modal>

    <!-- 编辑节点弹窗 -->
    <a-modal v-model:visible="editNodeVisible" title="编辑节点" width="500px" @ok="submitEditNode">
      <a-form :model="editNodeForm" layout="vertical">
        <a-form-item label="渠道 / 模型">
          <a-input :model-value="editNodeForm._label" disabled />
        </a-form-item>
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="权重">
              <a-input-number v-model="editNodeForm.weight" :min="1" :max="100" style="width:100%" />
              <div class="form-hint">同优先级内按权重加权随机分配请求</div>
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="优先级">
              <a-input-number v-model="editNodeForm.priority" :min="0" :max="100" style="width:100%" />
              <div class="form-hint">数字越小越优先，高优先级故障才降级</div>
            </a-form-item>
          </a-col>
        </a-row>
        <a-form-item label="启用">
          <a-switch v-model="editNodeForm.enabled" />
        </a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>

<script setup>
import { ref, onMounted, reactive, computed } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconPlus, IconDelete } from '@arco-design/web-vue/es/icon'
import { channelApi } from '../../api'

// ===== 数据 =====
const chains = ref([])
const channels = ref([])
const channelModelsMap = ref({})  // channelId -> [modelId1, modelId2, ...]
const loading = ref(false)

const nodeColumns = [
  { title: '渠道', slotName: 'channelName', width: 160 },
  { title: '原始模型ID', dataIndex: 'originalModelId', width: 170 },
  { title: '权重', slotName: 'weight', width: 140 },
  { title: '优先级', slotName: 'priority', width: 80 },
  { title: '状态', slotName: 'enabled', width: 70 },
  { title: '操作', slotName: 'action', width: 130 }
]

function getSupplierColor(type) {
  const map = { OpenAI: 'green', Azure: '#0078d4', Anthropic: 'orangered', Google: 'blue', DeepSeek: 'cyan', Groq: 'orange', Together: 'magenta', Custom: 'gray' }
  return map[type] || 'gray'
}
function getSupplierName(type) {
  const map = { OpenAI: 'OpenAI', Azure: 'Azure', Anthropic: 'Claude', Google: 'Gemini', DeepSeek: 'DeepSeek', Groq: 'Groq', Together: 'Together', Custom: '自定义' }
  return map[type] || type
}
function getChannelName(id) {
  return channels.value.find(c => c.id === id)?.name || '未知'
}
function getChainTypeColor(chain) {
  // 后端回传链类型，回退兼容取节点首项
  const t = chain.chainType || chain.nodes?.[0]?.chainType || 'Text'
  return t === 'Image' ? 'orangered' : 'arcoblue'
}
function getChainTypeLabel(chain) {
  const t = chain.chainType || chain.nodes?.[0]?.chainType || 'Text'
  return t === 'Image' ? '图片' : '文本'
}
function getChannelModels(channelId) {
  return channelModelsMap.value[channelId] || []
}

// 判断渠道是否为图片渠道（supportedPaths 含 images）
function isImageChannel(ch) {
  return (ch.supportedPaths || '').split(',').map(s => s.trim()).includes('images')
}
// 按链类型过滤可选渠道：文本链只可选文本 LLM 渠道，图片链只可选图片渠道
function getChannelsForChainType(chainType) {
  return channels.value.filter(ch => chainType === 'Image' ? isImageChannel(ch) : !isImageChannel(ch))
}

function getEnabledCount(chain) {
  return chain.nodes.filter(n => n.enabled).length
}
function getTotalWeight(chain) {
  return chain.nodes.filter(n => n.enabled).reduce((s, n) => s + n.weight, 0)
}
function getPrioritySummary(chain) {
  const priorities = [...new Set(chain.nodes.map(n => n.priority))].sort((a, b) => a - b)
  if (priorities.length === 1) return `P${priorities[0]}`
  return priorities.map(p => `P${p}`).join(' → ')
}
function getWeightPercent(chain, node) {
  const total = getTotalWeight(chain)
  if (total === 0) return 0
  return Math.round((node.weight / total) * 100)
}

// ===== 加载 =====
async function loadAll() {
  loading.value = true
  try {
    const [chainRes, chRes] = await Promise.all([
      channelApi.getChains(),
      channelApi.list()
    ])
    if (chainRes.code === 200) {
      const data = (chainRes.data || []).map(chain => {
        chain.nodes = chain.nodes.map(n => {
          const ch = chRes.code === 200 ? (chRes.data || []).find(c => c.id === n.channelId) : null
          n._supplierType = ch?.supplierType || 'Custom'
          return n
        })
        return chain
      })
      chains.value = data
    }
    if (chRes.code === 200) {
      channels.value = chRes.data || []
      // 为每个渠道加载模型列表
      for (const ch of channels.value) {
        try {
          const modelRes = await channelApi.getModels(ch.id)
          if (modelRes.code === 200) {
            channelModelsMap.value[ch.id] = (modelRes.data || []).map(m => m.originalModelId)
          }
        } catch { channelModelsMap.value[ch.id] = [] }
      }
    }
  } finally { loading.value = false }
}

// ===== 创建向导 =====
const wizardVisible = ref(false)
const wizardStep = ref(0)
const wizardSaving = ref(false)
const wizardForm = reactive({
  customModelId: '',
  displayName: '',
  description: '',
  chainType: 'Text'
})
const wizardNodes = ref([])

const chainWizardSteps = computed(() => {
  const step = wizardStep.value
  const steps = [
    { title: '基本信息' },
    { title: '选择节点' },
    { title: '确认创建' }
  ]
  return steps.map((s, i) => {
    if (i < step) s.status = 'finish'
    else if (i === step) s.status = 'process'
    else s.status = 'wait'
    return s
  })
})

function showCreateWizard() {
  wizardStep.value = 0
  wizardVisible.value = true
  Object.assign(wizardForm, { customModelId: '', displayName: '', description: '', chainType: 'Text' })
  wizardNodes.value = []
}

function addNodeRow() {
  wizardNodes.value.push({
    channelId: null,
    originalModelId: '',
    weight: 1,
    priority: 0,
    enabled: true
  })
}

function removeNodeRow(idx) {
  wizardNodes.value.splice(idx, 1)
}

function onNodeChannelChange(idx, channelId) {
  // 切换渠道时清空已选模型
  wizardNodes.value[idx].originalModelId = ''
}

// 链类型切换：清空已选的不匹配链类型的渠道节点，防止误提交错误类型
function onChainTypeChange() {
  const allow = (ch) => wizardForm.chainType === 'Image' ? isImageChannel(ch) : !isImageChannel(ch)
  wizardNodes.value = wizardNodes.value.filter(node => {
    if (!node.channelId) return true
    const ch = channels.value.find(c => c.id === node.channelId)
    return ch ? allow(ch) : true
  })
}

const groupedNodes = computed(() => {
  const groups = {}
  for (const node of wizardNodes.value) {
    const p = node.priority || 0
    if (!groups[p]) groups[p] = { priority: p, nodes: [] }
    groups[p].nodes.push(node)
  }
  return Object.values(groups).sort((a, b) => a.priority - b.priority)
})

function getMinPriority() {
  if (wizardNodes.value.length === 0) return 0
  return Math.min(...wizardNodes.value.map(n => n.priority || 0))
}
function getMaxPriority() {
  if (wizardNodes.value.length === 0) return 0
  return Math.max(...wizardNodes.value.map(n => n.priority || 0))
}

function wizardNext() {
  if (wizardStep.value === 0) {
    if (!wizardForm.customModelId.trim()) { Message.warning('请填写自定义模型 ID'); return }
    wizardStep.value = 1
    return
  }
  if (wizardStep.value === 1) {
    if (wizardNodes.value.length === 0) { Message.warning('请至少添加一个节点'); return }
    for (const node of wizardNodes.value) {
      if (!node.channelId) { Message.warning('每个节点都需要选择渠道'); return }
      if (!node.originalModelId.trim()) { Message.warning('每个节点都需要填写原始模型 ID'); return }
    }
    wizardStep.value = 2
    return
  }
}

async function submitChain() {
  wizardSaving.value = true
  try {
    // 批量创建所有节点，同一个 customModelId
    for (const node of wizardNodes.value) {
      await channelApi.createChain({
        customModelId: wizardForm.customModelId,
        displayName: wizardForm.displayName || null,
        chainType: wizardForm.chainType,
        channelId: node.channelId,
        originalModelId: node.originalModelId,
        weight: node.weight,
        priority: node.priority,
        enabled: node.enabled
      })
    }
    Message.success(`模型链「${wizardForm.customModelId}」创建成功！`)
    wizardVisible.value = false
    await loadAll()
  } catch (e) { console.error(e) } finally { wizardSaving.value = false }
}

// ===== 添加节点到已有链 =====
const addNodeVisible = ref(false)
const addNodeTarget = ref(null)
const addNodeChainType = ref('Text')
const addNodeForm = reactive({
  channelId: null,
  originalModelId: '',
  weight: 1,
  priority: 0,
  enabled: true
})

function addNodeToChain(chain) {
  addNodeTarget.value = chain.customModelId
  addNodeChainType.value = chain.chainType || 'Text'
  Object.assign(addNodeForm, {
    channelId: null, originalModelId: '', weight: 1, priority: 0, enabled: true
  })
  addNodeVisible.value = true
}

function onAddNodeChannelChange(channelId) {
  // 切换渠道时清空模型选择
  addNodeForm.originalModelId = ''
}

async function submitAddNode() {
  if (!addNodeForm.channelId || !addNodeForm.originalModelId.trim()) {
    Message.warning('请选择渠道并填写模型ID'); return
  }
  await channelApi.createChain({
    customModelId: addNodeTarget.value,
    chainType: addNodeChainType.value,
    channelId: addNodeForm.channelId,
    originalModelId: addNodeForm.originalModelId,
    weight: addNodeForm.weight,
    priority: addNodeForm.priority,
    enabled: addNodeForm.enabled
  })
  Message.success('节点添加成功')
  addNodeVisible.value = false
  await loadAll()
}

// ===== 编辑节点 =====
const editNodeVisible = ref(false)
const editNodeForm = reactive({
  id: null, weight: 1, priority: 0, enabled: true, _label: ''
})

function editNode(record) {
  editNodeForm.id = record.id
  editNodeForm.weight = record.weight
  editNodeForm.priority = record.priority
  editNodeForm.enabled = record.enabled
  const ch = channels.value.find(c => c.id === record.channelId)
  editNodeForm._label = `${ch?.name || '未知'} → ${record.originalModelId || '?'}`
  editNodeVisible.value = true
}

async function submitEditNode() {
  try {
    await channelApi.updateChain(editNodeForm.id, {
      weight: editNodeForm.weight,
      priority: editNodeForm.priority,
      enabled: editNodeForm.enabled
    })
    Message.success('节点更新成功')
    editNodeVisible.value = false
    await loadAll()
  } catch (e) {
    Message.error('更新失败: ' + (e.response?.data?.message || e.message))
  }
}

// ===== 操作 =====
async function toggleNode(node, val) {
  await channelApi.updateChain(node.id, { ...node, enabled: val })
  node.enabled = val
}

async function deleteNode(id) {
  await channelApi.deleteChain(id)
  Message.success('节点已删除')
  await loadAll()
}

async function deleteChain(customModelId) {
  // 删除该链的所有节点
  const chain = chains.value.find(c => c.customModelId === customModelId)
  if (!chain) return
  for (const node of chain.nodes) {
    await channelApi.deleteChain(node.id)
  }
  Message.success('模型链已删除')
  await loadAll()
}

onMounted(loadAll)
</script>

<style scoped>
.page-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 24px;
  flex-wrap: wrap;
  gap: 12px;
}
.page-header h2 { margin: 0; }
.page-desc { font-size: 13px; color: var(--color-text-3); display: block; margin-top: 4px; }

.empty-state { text-align: center; padding: 80px 40px; }
.empty-icon { font-size: 48px; margin-bottom: 16px; }
.empty-title { font-size: 16px; font-weight: 500; color: var(--color-text-2); margin-bottom: 8px; }
.empty-desc { font-size: 13px; color: var(--color-text-3); }

/* 链卡片 */
.chain-card {
  background: var(--color-bg-2);
  border: 1px solid var(--color-border-2);
  border-radius: 10px;
  padding: 20px;
  margin-bottom: 16px;
}
.chain-card-header {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  margin-bottom: 16px;
  flex-wrap: wrap;
  gap: 8px;
}
.chain-info { flex: 1; }
.chain-title {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 4px;
}
.chain-id {
  font-family: 'SF Mono', 'Fira Code', monospace;
  font-size: 15px;
  font-weight: 600;
  color: rgb(var(--primary-6));
}
.chain-routing-hint {
  font-size: 12px;
  color: var(--color-text-3);
}

/* 供应商色点 */
.supplier-dot {
  display: inline-block;
  width: 8px; height: 8px;
  border-radius: 50%;
  background: var(--color-text-3);
}
.supplier-dot.OpenAI { background: #10a37f; }
.supplier-dot.Azure { background: #0078d4; }
.supplier-dot.Anthropic { background: #d97757; }
.supplier-dot.Google { background: #4285f4; }
.supplier-dot.DeepSeek { background: #4d6bfe; }
.supplier-dot.Groq { background: #f97316; }
.supplier-dot.Together { background: #0f9; }

/* 权重条 */
.weight-bar {
  position: relative;
  width: 100%;
  height: 20px;
  background: var(--color-fill-2);
  border-radius: 4px;
  overflow: hidden;
}
.weight-fill {
  height: 100%;
  background: rgb(var(--primary-6));
  opacity: 0.3;
  border-radius: 4px;
  transition: width 0.3s;
}
.weight-text {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  font-size: 11px;
  font-weight: 500;
  color: var(--color-text-1);
}

/* 向导 */
.wizard-footer {
  display: flex; justify-content: flex-end; gap: 8px;
  margin-top: 24px; padding-top: 16px; border-top: 1px solid var(--color-border-2);
}
.form-hint { font-size: 12px; color: var(--color-text-3); margin-top: 2px; }

/* 节点行 */
.nodes-section { min-height: 200px; }
.nodes-header {
  display: flex; justify-content: space-between; align-items: center;
  margin-bottom: 12px; font-size: 13px; color: var(--color-text-2);
}
.nodes-empty {
  text-align: center; padding: 40px; color: var(--color-text-3);
  border: 2px dashed var(--color-border-2); border-radius: 8px;
}
.node-row {
  display: flex; align-items: center; gap: 8px;
  padding: 10px; margin-bottom: 8px;
  background: var(--color-fill-1); border-radius: 6px;
}
.node-row-number {
  width: 28px; height: 28px; border-radius: 6px;
  background: rgb(var(--primary-6)); color: #fff;
  display: flex; align-items: center; justify-content: center;
  font-size: 12px; font-weight: 600; flex-shrink: 0;
}
.node-row-fields { flex: 1; }
.weight-input-group { display: flex; align-items: center; gap: 4px; }
.weight-label { font-size: 11px; color: var(--color-text-3); white-space: nowrap; }

/* 路由预览 */
.nodes-preview {
  margin-top: 16px;
  background: var(--color-fill-1);
  border-radius: 8px;
  padding: 16px;
}
.preview-title { font-size: 13px; font-weight: 500; margin-bottom: 10px; color: var(--color-text-2); }
.preview-flow { }
.flow-arrow {
  text-align: center; font-size: 13px; color: var(--color-text-3);
  padding: 4px 0;
}
.priority-group { margin-bottom: 6px; }
.priority-label {
  font-size: 11px; font-weight: 600; color: rgb(var(--primary-6));
  margin-bottom: 4px;
}
.priority-nodes { display: flex; flex-wrap: wrap; gap: 6px; }
.priority-node {
  font-size: 12px; padding: 2px 8px;
  background: var(--color-bg-2); border-radius: 4px;
  border: 1px solid var(--color-border-2);
}
.node-weight { color: var(--color-text-3); }

/* 确认 */
.confirm-nodes { margin-top: 16px; }
.confirm-node {
  display: flex; align-items: center; gap: 6px;
  padding: 6px 0; font-size: 13px;
}
.confirm-idx { color: var(--color-text-3); width: 24px; }
.confirm-arrow { color: var(--color-text-3); }
.confirm-meta { color: var(--color-text-3); font-size: 12px; margin-left: auto; }
</style>
