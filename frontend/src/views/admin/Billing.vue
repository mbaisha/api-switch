<template>
  <div class="billing-page">
    <div class="page-header">
      <h2>账单管理</h2>
    </div>

    <a-tabs default-active-key="records">
      <a-tab-pane key="records" title="账单记录">
        <a-alert type="info" style="margin-bottom: 16px">
          模型价格在「定价规则」标签页设置。未配置价格的模型将使用默认定价（输入 ¥0.01/千Token，输出 ¥0.03/千Token）。
        </a-alert>
        <!-- 筛选 -->
        <a-row :gutter="16" class="filter-row">
          <a-col :span="4">
            <a-input v-model="filter.tokenId" placeholder="令牌ID" allow-clear />
          </a-col>
          <a-col :span="4">
            <a-input v-model="filter.modelId" placeholder="模型ID" allow-clear />
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="filter.startTime" placeholder="开始时间" style="width:100%" />
          </a-col>
          <a-col :span="4">
            <a-date-picker v-model="filter.endTime" placeholder="结束时间" style="width:100%" />
          </a-col>
          <a-col :span="4">
            <a-space>
              <a-button type="primary" @click="loadRecords()">查询</a-button>
              <a-button @click="loadSummary()">汇总</a-button>
            </a-space>
          </a-col>
        </a-row>

        <!-- 汇总卡片 -->
        <a-row :gutter="16" class="summary-row" v-if="summary.totalRecords > 0">
          <a-col :span="6"><a-card><a-statistic title="总记录数" :value="summary.totalRecords" /></a-card></a-col>
          <a-col :span="6"><a-card><a-statistic title="总消费" :value="summary.totalCost" :precision="4" prefix="¥" /></a-card></a-col>
          <a-col :span="6"><a-card><a-statistic title="总输入Token" :value="summary.totalInputTokens" /></a-card></a-col>
          <a-col :span="6"><a-card><a-statistic title="总输出Token" :value="summary.totalOutputTokens" /></a-card></a-col>
        </a-row>

        <a-table :columns="recordColumns" :data="records" row-key="id" :loading="loading" :pagination="false" class="table">
          <template #createdAt="{ record }">
            {{ formatBeijingTime(record.createdAt) }}
          </template>
          <template #inputPrice="{ record }">
            ¥{{ (record.inputPrice || 0).toFixed(6) }}
          </template>
          <template #outputPrice="{ record }">
            ¥{{ (record.outputPrice || 0).toFixed(6) }}
          </template>
          <template #cost="{ record }">
            <span style="font-weight:600;color:rgb(var(--warning-6))">¥{{ (record.cost || 0).toFixed(6) }}</span>
          </template>
        </a-table>
        <a-pagination
          :total="total" :current="page" :page-size="pageSize"
          @change="onPageChange" style="margin-top:16px"
        />
      </a-tab-pane>

      <a-tab-pane key="rules" title="定价规则">
        <div style="margin-bottom:16px">
          <a-button type="primary" @click="showRuleDialog">新增规则</a-button>
        </div>
        <a-table :columns="ruleColumns" :data="rules" row-key="id" :loading="rulesLoading">
          <template #inputPrice="{ record }">
            ¥{{ (record.inputPrice || 0).toFixed(6) }}
          </template>
          <template #outputPrice="{ record }">
            ¥{{ (record.outputPrice || 0).toFixed(6) }}
          </template>
          <template #action="{ record }">
            <a-space>
              <a-button size="small" @click="editRule(record)">编辑</a-button>
              <a-popconfirm content="确认删除?" @ok="deleteRule(record.id)">
                <a-button size="small" status="danger">删除</a-button>
              </a-popconfirm>
            </a-space>
          </template>
        </a-table>
      </a-tab-pane>
    </a-tabs>

    <!-- 定价规则弹窗 -->
    <a-modal v-model:visible="ruleDialogVisible" :title="isEditRule ? '编辑定价规则' : '新增定价规则'" @ok="saveRule">
      <a-form :model="ruleForm" layout="vertical">
        <a-form-item label="令牌ID(0=全局默认)">
          <a-input-number v-model="ruleForm.tokenId" :min="0" style="width:100%" />
        </a-form-item>
        <a-form-item label="自定义模型ID" required>
          <a-input v-model="ruleForm.customModelId" placeholder="如：gpt-4o" />
        </a-form-item>
        <a-row :gutter="16">
          <a-col :span="12">
            <a-form-item label="输入单价(元/千Token)">
              <a-input-number v-model="ruleForm.inputPrice" :min="0" :precision="6" style="width:100%" placeholder="0.01" />
            </a-form-item>
          </a-col>
          <a-col :span="12">
            <a-form-item label="输出单价(元/千Token)">
              <a-input-number v-model="ruleForm.outputPrice" :min="0" :precision="6" style="width:100%" placeholder="0.03" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-form-item label="启用">
          <a-switch v-model="ruleForm.enabled" />
        </a-form-item>
      </a-form>
    </a-modal>
  </div>
</template>

<script setup>
import { ref, onMounted } from 'vue'
import { Message } from '@arco-design/web-vue'
import { billingApi } from '../../api'
import { formatBeijingTime } from '../../utils/date'

const records = ref([])
const loading = ref(false)
const page = ref(1)
const pageSize = ref(20)
const total = ref(0)

const filter = ref({ tokenId: '', modelId: '', startTime: '', endTime: '' })
const summary = ref({ totalRecords: 0, totalCost: 0, totalInputTokens: 0, totalOutputTokens: 0 })

const recordColumns = [
  { title: 'ID', dataIndex: 'id', width: 60 },
  { title: '令牌', dataIndex: 'tokenValue', width: 180 },
  { title: '备注', dataIndex: 'tokenRemark', ellipsis: true, width: 100 },
  { title: '模型', dataIndex: 'customModelId', width: 120 },
  { title: '输入Token', dataIndex: 'inputTokens', width: 90 },
  { title: '输出Token', dataIndex: 'outputTokens', width: 90 },
  { title: '输入单价', slotName: 'inputPrice', width: 100 },
  { title: '输出单价', slotName: 'outputPrice', width: 100 },
  { title: '消费金额', slotName: 'cost', width: 110 },
  { title: '时间', slotName: 'createdAt', width: 160 }
]

async function loadRecords(pg = 1) {
  loading.value = true
  page.value = pg
  try {
    const params = { page: pg, pageSize: pageSize.value }
    if (filter.value.tokenId) params.tokenId = filter.value.tokenId
    if (filter.value.modelId) params.modelId = filter.value.modelId
    if (filter.value.startTime) params.startTime = filter.value.startTime
    if (filter.value.endTime) params.endTime = filter.value.endTime
    const res = await billingApi.records(params)
    if (res.code === 200) {
      records.value = res.data.list
      total.value = res.data.total
    }
  } finally { loading.value = false }
}

async function loadSummary() {
  try {
    const params = {}
    if (filter.value.tokenId) params.tokenId = filter.value.tokenId
    if (filter.value.startTime) params.startTime = filter.value.startTime
    if (filter.value.endTime) params.endTime = filter.value.endTime
    const res = await billingApi.summary(params)
    if (res.code === 200) summary.value = res.data
  } catch (e) { console.error(e) }
}

function onPageChange(pg) { loadRecords(pg) }

// 定价规则
const rules = ref([])
const rulesLoading = ref(false)
const ruleDialogVisible = ref(false)
const isEditRule = ref(false)
const ruleForm = ref(getDefaultRule())

function getDefaultRule() {
  return { tokenId: 0, customModelId: '', inputPrice: 0.01, outputPrice: 0.03, enabled: true }
}

async function loadRules() {
  rulesLoading.value = true
  try {
    const res = await billingApi.rules()
    if (res.code === 200) rules.value = res.data
  } finally { rulesLoading.value = false }
}

const ruleColumns = [
  { title: 'ID', dataIndex: 'id', width: 60 },
  { title: '令牌ID', dataIndex: 'tokenId', width: 120 },
  { title: '模型', dataIndex: 'customModelId' },
  { title: '输入单价', slotName: 'inputPrice', width: 120 },
  { title: '输出单价', slotName: 'outputPrice', width: 120 },
  { title: '启用', dataIndex: 'enabled', width: 60 },
  { title: '操作', slotName: 'action', width: 140 }
]

function showRuleDialog() {
  isEditRule.value = false
  ruleForm.value = getDefaultRule()
  ruleDialogVisible.value = true
}

function editRule(record) {
  isEditRule.value = true
  ruleForm.value = { ...record }
  ruleDialogVisible.value = true
}

async function saveRule() {
  try {
    if (isEditRule.value) {
      await billingApi.updateRule(ruleForm.value.id, ruleForm.value)
    } else {
      await billingApi.createRule(ruleForm.value)
    }
    Message.success(isEditRule.value ? '更新成功' : '创建成功')
    ruleDialogVisible.value = false
    await loadRules()
  } catch (e) { console.error(e) }
}

async function deleteRule(id) {
  await billingApi.deleteRule(id)
  Message.success('删除成功')
  await loadRules()
}

onMounted(() => {
  loadRecords()
  loadRules()
})
</script>

<style scoped>
.page-header { margin-bottom: 16px; }
.page-header h2 { margin: 0; }
.filter-row { margin-bottom: 16px; }
.summary-row { margin-bottom: 16px; }
.table { margin-top: 8px; }
</style>
