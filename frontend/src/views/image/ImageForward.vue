<template>
  <div class="image-forward-page">
    <div class="page-header">
      <div class="header-left">
        <h2>图片转发</h2>
        <span class="page-subtitle">上游对接配置 · 下游统一输出 /v1/images/generations</span>
      </div>
      <a-button type="primary" size="large" @click="showCreateWizard">
        <template #icon><icon-plus /></template>
        新增上游对接
      </a-button>
      <a-button size="large" type="outline" @click="openDownstreamTest">
        <template #icon><icon-experiment /></template>
        接口测试
      </a-button>
    </div>

    <!-- 下游统一口径说明 -->
    <a-card :bordered="false" class="standard-card" title="下游统一接口">
      <a-descriptions :column="1" size="small">
        <a-descriptions-item label="端点">
          <code>POST /v1/images/generations</code>
        </a-descriptions-item>
        <a-descriptions-item label="文生图">
          <code>{ "model", "prompt", "size", "response_format": "url|b64_json" }</code>
        </a-descriptions-item>
        <a-descriptions-item label="图生图 / 多图">
          <code>{ ..., "image": "url或base64" | ["url1","url2"] }</code>（image 字段扩展，支持多图数组）
        </a-descriptions-item>
        <a-descriptions-item label="返回">
          <code>{ "created", "data": [{ "url" | "b64_json" }] }</code>（原样透传，不代理落盘）
        </a-descriptions-item>
      </a-descriptions>
      <a-alert type="info" style="margin-top: 12px">
        所有上游平台经本系统转换后，对外口径完全一致。令牌需在「令牌管理」开通图片权限后方可调用。
      </a-alert>
    </a-card>

    <!-- 上游对接列表（复用 Channel 表，只展示支持 images 端点的渠道） -->
    <a-card :bordered="false" class="table-card" title="上游对接配置">
      <a-table :columns="columns" :data="channels" row-key="id" :loading="loading" :pagination="false">
        <template #supplierType="{ record }">
          <a-tag :color="getSupplierColor(record.supplierType)" size="small">{{ getSupplierName(record.supplierType) }}</a-tag>
        </template>
        <template #enabled="{ record }">
          <a-switch :model-value="record.enabled" size="small" @change="(v) => toggleChannel(record, v)" />
        </template>
        <template #modelCount="{ record }">
          <a-tag color="arcoblue" size="small">{{ record._modelCount ?? 0 }} 个模型</a-tag>
        </template>
        <template #action="{ record }">
          <a-space>
            <a-button size="small" type="text" @click="editChannel(record)">编辑</a-button>
            <a-button size="small" type="text" @click="manageChannelDetail(record)">管理</a-button>
            <a-popconfirm content="确认删除此对接?" @ok="deleteChannel(record.id)">
              <a-button size="small" type="text" status="danger">删除</a-button>
            </a-popconfirm>
          </a-space>
        </template>
      </a-table>
      <a-empty v-if="!loading && channels.length === 0" description="暂无上游对接，点击右上角「新增上游对接」配置各平台" />
    </a-card>

    <!-- ===== 新建/编辑上游对接弹窗 ===== -->
    <a-modal v-model:visible="wizardVisible" :title="editingChannelId ? '编辑上游对接' : '新增上游对接'" width="960px" :footer="false" :mask-closable="false" :body-style="{ maxHeight: 'calc(100vh - 140px)', overflowY: 'auto', padding: '12px 20px' }">
      <a-form :model="wizardForm" layout="vertical" size="small">
        <!-- 基础信息：4 字段横排一行 -->
        <a-row :gutter="8">
          <a-col :span="6">
            <a-form-item label="上游平台" required>
              <a-select v-model="wizardForm.supplierType" placeholder="选择" @change="onSupplierChange" allow-search>
                <a-option v-for="p in supplierPresets" :key="p.type" :value="p.type">{{ p.name }}</a-option>
              </a-select>
            </a-form-item>
          </a-col>
          <a-col :span="6">
            <a-form-item label="对接名称" required>
              <a-input v-model="wizardForm.name" placeholder="如：豆包生产" />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="API 地址 (Base URL)" required>
              <a-input v-model="wizardForm.apiAddress" :placeholder="apiPlaceholder" />
            </a-form-item>
          </a-col>
          <a-col :span="4">
            <a-form-item label="状态">
              <a-switch v-model="wizardForm.enabled">
                <template #checked>启用</template>
                <template #unchecked>禁用</template>
              </a-switch>
            </a-form-item>
          </a-col>
        </a-row>
        <div class="form-hint" style="margin-top:-10px;margin-bottom:4px">{{ apiHint }}</div>

        <!-- 讯飞专属 + 超时/冷却/备注 同行 -->
        <a-row :gutter="8">
          <template v-if="wizardForm.supplierType === 'Xfyun'">
            <a-col :span="7">
              <a-form-item label="讯飞 appId" required>
                <a-input v-model="wizardForm._xfyunAppId" placeholder="讯飞开放平台 appId" allow-clear />
              </a-form-item>
            </a-col>
            <a-col :span="7">
              <a-form-item label="讯飞 APISecret（第二密钥）" required>
                <a-input v-model="wizardForm._apiKey2" placeholder="HMAC 签名用" allow-clear />
              </a-form-item>
            </a-col>
          </template>
          <a-col :span="5">
            <a-form-item label="超时(秒)">
              <a-input-number v-model="wizardForm.timeoutSeconds" :min="5" :max="600" style="width:100%" />
            </a-form-item>
          </a-col>
          <a-col :span="5">
            <a-form-item label="冷却(秒)">
              <a-input-number v-model="wizardForm.cooldownSeconds" :min="0" :max="3600" style="width:100%" />
            </a-form-item>
          </a-col>
          <a-col v-if="wizardForm.supplierType !== 'Xfyun'" :span="14">
            <a-form-item label="备注">
              <a-input v-model="wizardForm.remark" placeholder="可选：链用途说明" />
            </a-form-item>
          </a-col>
        </a-row>

        <!-- 支持分辨率（图片大小限制） -->
        <a-divider orientation="left" style="margin: 4px 0 8px">支持分辨率（图片大小）</a-divider>
        <div style="display:flex;gap:6px;margin-bottom:6px">
          <a-input v-model="sizeInput" placeholder="如 720x1280" size="small" @keydown.enter.prevent="addSupportedSize" />
          <a-button size="small" type="outline" @click="addSupportedSize" :disabled="!sizeInput.trim()">+</a-button>
        </div>
        <div class="form-hint" style="margin-bottom:6px">
          填写后下游请求的 size 将按宽高比缩到最接近的支持尺寸；留空表示不限制，按客户端原样透传。
        </div>
        <div v-if="wizardForm._supportedSizeList.length > 0" style="display:flex;flex-wrap:wrap;gap:4px;margin-bottom:8px">
          <a-tag v-for="(sz, idx) in wizardForm._supportedSizeList" :key="idx" closable size="small" @close="wizardForm._supportedSizeList.splice(idx, 1)">
            <code style="font-size:11px">{{ sz }}</code>
          </a-tag>
        </div>

        <!-- API 密钥 + 模型映射 左右两栏 -->
        <a-row :gutter="12" style="margin-top:4px">
          <a-col :span="12">
            <a-divider orientation="left" style="margin: 4px 0 8px">API 密钥</a-divider>
            <div v-if="editingChannelId && existingKeys.length > 0" style="margin-bottom:8px">
              <div style="font-size:12px;color:var(--color-text-3);margin-bottom:4px">已有密钥 ({{ existingKeys.length }} 个)</div>
              <div class="existing-list">
                <div v-for="key in existingKeys" :key="key.id" class="existing-item">
                  <code class="key-text">{{ key.keyValue }}</code>
                  <a-button size="mini" type="text" status="danger" @click="removeExistingKey(key.id)">✕</a-button>
                </div>
              </div>
            </div>
            <a-textarea v-model="keysTextarea" placeholder="每行一个密钥&#10;sk-xxx1&#10;sk-xxx2" :rows="3" />
            <div class="form-hint" style="margin-top:2px">已识别 {{ parsedKeysCount }} 个新密钥</div>
          </a-col>
          <a-col :span="12">
            <a-divider orientation="left" style="margin: 4px 0 8px">模型映射</a-divider>
            <div v-if="wizardForm._availableModels.length > 0" style="margin-bottom:6px">
              <div style="font-size:12px;color:var(--color-text-3);margin-bottom:4px">推荐模型（勾选即添加）</div>
              <a-checkbox-group v-model="selectedPresetModels" direction="vertical">
                <a-checkbox v-for="m in wizardForm._availableModels" :key="m" :value="m">
                  <code style="font-size:11px">{{ m }}</code>
                </a-checkbox>
              </a-checkbox-group>
            </div>
            <div style="display:flex;gap:6px;margin-bottom:6px">
              <a-input v-model="manualModelInput" placeholder="原始模型ID" size="small" @keydown.enter.prevent="addManualModel" />
              <a-input v-model="modelBatchCustomId" placeholder="自定义模型ID(可空)" size="small" />
              <a-button size="small" type="outline" @click="addManualModel" :disabled="!manualModelInput.trim()">+</a-button>
            </div>
            <div v-if="manualModels.length > 0" style="display:flex;flex-wrap:wrap;gap:4px">
              <a-tag v-for="(m, idx) in manualModels" :key="idx" closable size="small" @close="manualModels.splice(idx, 1)">
                {{ m }} → {{ modelBatchCustomId || m }}
              </a-tag>
            </div>
          </a-col>
        </a-row>

        <!-- ===== 上游接口测试（折叠，默认收起） ===== -->
        <a-collapse :default-active-key="[]" style="margin-top:8px">
          <a-collapse-item key="test" header="上游接口测试 ▶（点击展开，用本渠道密钥直测上游）">
            <div class="test-panel">
              <div class="test-hint">直接调用上游平台 <code>/images/generations</code> 验证对接是否可用。仅使用本渠道密钥与已选原始模型ID，不经过令牌/权限/计费。</div>
              <a-row :gutter="8">
                <a-col :span="10">
                  <a-form-item label="原始模型ID" layout="vertical">
                    <a-select v-model="upstreamTestForm.modelId" placeholder="从已有模型中选择" size="small" allow-search allow-clear>
                      <a-option v-for="m in upstreamTestModelOptions" :key="m" :value="m">{{ m }}</a-option>
                    </a-select>
                  </a-form-item>
                </a-col>
                <a-col :span="6">
                  <a-form-item label="尺寸 size" layout="vertical">
                    <a-input v-model="upstreamTestForm.size" placeholder="1024x1024" size="small" />
                  </a-form-item>
                </a-col>
                <a-col :span="4">
                  <a-form-item label="返回格式" layout="vertical">
                    <a-select v-model="upstreamTestForm.responseFormat" size="small">
                      <a-option value="url">url</a-option>
                      <a-option value="b64_json">b64_json</a-option>
                    </a-select>
                  </a-form-item>
                </a-col>
                <a-col :span="4">
                  <a-form-item label="&nbsp;" layout="vertical">
                    <a-button type="primary" size="small" :loading="upstreamTestRunning" @click="runUpstreamTest" long>
                      <template #icon><icon-experiment /></template>
                      测试
                    </a-button>
                  </a-form-item>
                </a-col>
              </a-row>
              <a-row :gutter="8">
                <a-col :span="20">
                  <a-form-item label="提示词 prompt" layout="vertical">
                    <a-input v-model="upstreamTestForm.prompt" placeholder="如: 一只柴犬" size="small" />
                  </a-form-item>
                </a-col>
                <a-col :span="4">
                  <a-form-item label="&nbsp;" layout="vertical">
                    <a-button size="small" type="outline" @click="loadUpstreamTestModels" long>刷新模型列表</a-button>
                  </a-form-item>
                </a-col>
              </a-row>

              <div v-if="upstreamTestResult" class="test-section">
                <div class="test-section-title">▼ 发送到上游</div>
                <div style="font-size:12px;color:var(--color-text-3);margin-bottom:4px">{{ upstreamTestResult.upstreamUrl }}</div>
                <pre class="test-payload">{{ upstreamTestResult.request }}</pre>
              </div>
              <div v-if="upstreamTestResult" class="test-section">
                <div class="test-section-title">
                  ▼ 上游响应
                  <a-tag :color="upstreamTestResult.ok ? 'green' : 'red'" size="small">{{ upstreamTestResult.ok ? '成功' : '失败' }} · {{ upstreamTestResult.code }} · {{ upstreamTestResult.duration }}ms</a-tag>
                </div>
                <pre class="test-payload">{{ upstreamTestResult.response }}</pre>
              </div>
              <div v-if="upstreamTestResult && upstreamTestResult.images.length > 0" class="test-section">
                <div class="test-section-title">▼ 上游返回图片 ({{ upstreamTestResult.images.length }} 张)</div>
                <div class="test-images">
                  <div v-for="(img, idx) in upstreamTestResult.images" :key="idx" class="test-image-item">
                    <img :src="img.src" :alt="`图${idx+1}`" style="max-width:140px;max-height:140px;border:1px solid var(--color-border-2);border-radius:4px" @error="(e)=>e.target.style.display='none'" />
                    <div class="test-image-meta">
                      <a-tag size="small" :color="img.type === 'url' ? 'arcoblue' : 'orangered'">{{ img.type }}</a-tag>
                      <a-button v-if="img.type === 'url'" size="mini" type="text" @click="copyText(img.src)">复制</a-button>
                    </div>
                  </div>
                </div>
              </div>
            </div>
          </a-collapse-item>
        </a-collapse>
      </a-form>

      <div class="wizard-footer" style="position:sticky;bottom:0;background:var(--color-bg-1);z-index:2">
        <a-button @click="wizardVisible = false">取消</a-button>
        <a-button type="primary" :loading="wizardSaving" @click="submitWizard">{{ editingChannelId ? '保存' : '完成创建' }}</a-button>
      </div>
    </a-modal>

    <!-- ===== 对接详情管理弹窗 ===== -->
    <a-modal v-model:visible="detailVisible" :title="`管理对接: ${detailChannel?.name}`" width="900px" :footer="false">
      <a-tabs v-if="detailChannel">
        <a-tab-pane key="keys" title="API 密钥">
          <div style="margin-bottom: 12px">
            <a-space>
              <a-input v-model="newKeyValue" placeholder="输入 API Key" style="width: 400px" @keyup.enter="addSingleKey" />
              <a-button type="primary" size="small" @click="addSingleKey" :disabled="!newKeyValue">添加</a-button>
            </a-space>
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
              <a-col :span="6"><a-input v-model="newModelForm.originalModelId" placeholder="原始模型ID" size="small" /></a-col>
              <a-col :span="6"><a-input v-model="newModelForm.modelName" placeholder="显示名称" size="small" /></a-col>
              <a-col :span="6"><a-input v-model="newModelForm.customModelId" placeholder="自定义模型ID" size="small" /></a-col>
              <a-col :span="3"><a-input-number v-model="newModelForm.weight" :min="1" :max="100" size="small" style="width:100%" /></a-col>
              <a-col :span="3"><a-button type="primary" size="small" @click="addSingleModel" long>添加</a-button></a-col>
            </a-row>
          </div>
          <a-table :columns="modelColumns" :data="detailModels" row-key="id" size="small" :pagination="false">
            <template #action="{ record }">
              <a-button size="mini" type="text" status="danger" @click="deleteModel(record.id)">删除</a-button>
            </template>
          </a-table>
        </a-tab-pane>
      </a-tabs>
    </a-modal>

    <!-- ===== 下游接口测试弹窗（独立菜单，用令牌+自定义模型ID测对外端点） ===== -->
    <a-modal v-model:visible="downstreamTestVisible" title="接口测试 · 对外端点 /v1/images/generations" width="760px" :footer="false" :mask-closable="false">
      <a-alert type="info" style="margin-bottom:12px">
        此处用令牌与对外自定义模型ID走完整的下游链路（鉴权/链/计费/日志），用于验证整套服务对外可用。
        验证上游对接是否可用请在「编辑上游对接」内使用上游接口测试。
      </a-alert>
      <a-form :model="testForm" layout="vertical" size="small">
        <a-row :gutter="8">
          <a-col :span="10">
            <a-form-item label="令牌" required>
              <a-input v-model="testForm.token" placeholder="sk-xxx" allow-clear />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="模型 customModelId" required>
              <a-input v-model="testForm.model" placeholder="如 doubao-seedream" />
            </a-form-item>
          </a-col>
          <a-col :span="6">
            <a-form-item label="尺寸 size">
              <a-input v-model="testForm.size" placeholder="1024x1024" />
            </a-form-item>
          </a-col>
        </a-row>
        <a-row :gutter="8">
          <a-col :span="14">
            <a-form-item label="提示词 prompt" required>
              <a-input v-model="testForm.prompt" placeholder="如: 一只柴犬" />
            </a-form-item>
          </a-col>
          <a-col :span="5">
            <a-form-item label="返回格式">
              <a-select v-model="testForm.responseFormat">
                <a-option value="url">url</a-option>
                <a-option value="b64_json">b64_json</a-option>
              </a-select>
            </a-form-item>
          </a-col>
          <a-col :span="5">
            <a-form-item label="&nbsp;">
              <a-button type="primary" :loading="testRunning" @click="runTest" long>
                <template #icon><icon-experiment /></template>
                测试
              </a-button>
            </a-form-item>
          </a-col>
        </a-row>
        <a-form-item label="参考图 image (可选，URL 或 Base64，多图每行一个)">
          <a-textarea v-model="testForm.image" :rows="2" placeholder="https://example.com/ref.png
base64,iVBORw0KG..." />
        </a-form-item>
      </a-form>

      <div v-if="testResult" class="test-section">
        <div class="test-section-title">▼ 发送内容</div>
        <pre class="test-payload">{{ testResult.request }}</pre>
      </div>
      <div v-if="testResult" class="test-section">
        <div class="test-section-title">
          ▼ 回显内容
          <a-tag :color="testResult.ok ? 'green' : 'red'" size="small">{{ testResult.ok ? '成功' : '失败' }} · {{ testResult.code }} · {{ testResult.duration }}ms</a-tag>
        </div>
        <pre class="test-payload">{{ testResult.response }}</pre>
      </div>
      <div v-if="testResult && testResult.images.length > 0" class="test-section">
        <div class="test-section-title">▼ 获取到的图片 ({{ testResult.images.length }} 张)</div>
        <div class="test-images">
          <div v-for="(img, idx) in testResult.images" :key="idx" class="test-image-item">
            <img :src="img.src" :alt="`图${idx+1}`" style="max-width:140px;max-height:140px;border:1px solid var(--color-border-2);border-radius:4px" @error="(e)=>e.target.style.display='none'" />
            <div class="test-image-meta">
              <a-tag size="small" :color="img.type === 'url' ? 'arcoblue' : 'orangered'">{{ img.type }}</a-tag>
              <a-button v-if="img.type === 'url'" size="mini" type="text" @click="copyText(img.src)">复制</a-button>
            </div>
          </div>
        </div>
      </div>
    </a-modal>
  </div>
</template>

<script setup>
import { ref, onMounted, computed, reactive } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconPlus, IconExperiment } from '@arco-design/web-vue/es/icon'
import { channelApi } from '../../api'
import axios from 'axios'

// ===== 数据 =====
const channels = ref([])
const loading = ref(false)
const supplierPresets = ref([])

const columns = [
  { title: 'ID', dataIndex: 'id', width: 50 },
  { title: '对接名称', dataIndex: 'name', width: 180 },
  { title: '上游平台', slotName: 'supplierType', width: 120 },
  { title: '模型数', slotName: 'modelCount', width: 100 },
  { title: '状态', slotName: 'enabled', width: 70 },
  { title: '操作', slotName: 'action', width: 220, fixed: 'right' }
]

// 仅展示图片类供应商预设
const imageSupplierTypes = ['VolcEngine', 'SiliconFlow', 'Agnes', 'ModelScope', 'SenseNova', 'Xfyun', 'Gitee', 'DashScope', 'OpenAI', 'Azure', 'Custom']

function getSupplierColor(type) {
  const map = { OpenAI: '#10a37f', Azure: '#0078d4', Custom: 'gray',
    VolcEngine: '#ff5a00', SiliconFlow: '#6f42c1', Agnes: '#e91e63', ModelScope: '#0052cc', SenseNova: '#21a675', Xfyun: '#b34975', Gitee: '#c71d23', DashScope: '#ff6a00' }
  return map[type] || 'gray'
}
function getSupplierName(type) {
  const map = { OpenAI: 'OpenAI', Azure: 'Azure', Custom: '自定义',
    VolcEngine: '豆包/火山', SiliconFlow: '硅基', Agnes: 'Agnes', ModelScope: '魔搭', SenseNova: '商汤', Xfyun: '讯飞', Gitee: 'Gitee', DashScope: '百炼' }
  return map[type] || type
}

const wizardVisible = ref(false)
const wizardSaving = ref(false)
const editingChannelId = ref(null)
const wizardForm = reactive({
  name: '', remark: '', supplierType: 'VolcEngine', apiAddress: '',
  timeoutSeconds: 120, cooldownSeconds: 60, enabled: true,
  _xfyunAppId: '', _apiKey2: '', _availableModels: [],
  _supportedSizeList: [] // 支持分辨率列表（如 ["720x1280"]），空=不限制
})
const sizeInput = ref('')
function addSupportedSize() {
  const v = sizeInput.value.trim()
  if (!v) return
  // 基础校验：须为 WxH 数字格式
  if (!/^\d+\s*[x×]\s*\d+$/i.test(v)) { Message.warning('格式应为 宽x高，如 720x1280'); return }
  const normalized = v.replace(/\s/g, '').replace('×', 'x').toLowerCase()
  if (!wizardForm._supportedSizeList.includes(normalized)) wizardForm._supportedSizeList.push(normalized)
  sizeInput.value = ''
}
const keysTextarea = ref('')
const modelBatchCustomId = ref('')
const selectedPresetModels = ref([])
const manualModelInput = ref('')
const manualModels = ref([])
const existingKeys = ref([])
const existingModels = ref([])

const parsedKeysCount = computed(() => keysTextarea.value.split('\n').map(k => k.trim()).filter(Boolean).length)
const apiPlaceholder = computed(() => {
  const p = supplierPresets.value.find(x => x.type === wizardForm.supplierType)
  return p?.defaultApi ? `如: ${p.defaultApi}` : 'https://your-api.example.com/v1'
})
const apiHint = computed(() => {
  switch (wizardForm.supplierType) {
    case 'VolcEngine': return '豆包/火山 Seedream，OpenAI 兼容，文生图与图生图共用 /images/generations，image 字段传参考图（最多10张）'
    case 'SiliconFlow': return '硅基流动，OpenAI 兼容，size 自动映射为 image_size，多图自动拆为 image/image2/image3'
    case 'Agnes': return 'Agnes-Ai，OpenAI 兼容，image 与 response_format 自动塞进 extra_body，return_base64 控制 base64 返回'
    case 'ModelScope': return '魔搭，OpenAI 兼容，图生图用 images 字段（base64 数组），URL 参考图自动下载转 base64，走异步任务模式'
    case 'SenseNova': return '商汤 U1，OpenAI 兼容，size 限白名单 11 个值，图生图自动包装为 chat/completions + modalities:image'
    case 'Xfyun': return '讯飞星辰 MaaS，三段式请求体，Bearer apikey:apisecret 鉴权（不用 HMAC 签名），header.patch_id 必填，HiDream 图生图走异步任务'
    case 'Gitee': return 'Gitee AI，OpenAI 兼容，/images/generations + /images/edits'
    case 'DashScope': return '阿里云百炼，通义万相，走 services/aigc 异步任务模式'
    default: return '系统将自动拼接正确的接口路径'
  }
})

function showCreateWizard() {
  editingChannelId.value = null
  wizardVisible.value = true
  Object.assign(wizardForm, { name: '', remark: '', supplierType: 'VolcEngine', apiAddress: '', timeoutSeconds: 120, cooldownSeconds: 60, enabled: true, _xfyunAppId: '', _apiKey2: '', _availableModels: [], _supportedSizeList: [] })
  sizeInput.value = ''
  keysTextarea.value = ''
  modelBatchCustomId.value = ''
  selectedPresetModels.value = []
  manualModels.value = []
  manualModelInput.value = ''
  existingKeys.value = []
  existingModels.value = []
  onSupplierChange()
}

function onSupplierChange() {
  const p = supplierPresets.value.find(x => x.type === wizardForm.supplierType)
  wizardForm._availableModels = p?.defaultModels || []
  if (!editingChannelId.value) wizardForm.apiAddress = p?.defaultApi || ''
}

function addManualModel() {
  const val = manualModelInput.value.trim()
  if (!val) return
  if (!manualModels.value.includes(val)) manualModels.value.push(val)
  manualModelInput.value = ''
}

async function submitWizard() {
  if (!wizardForm.name.trim()) { Message.warning('请填写对接名称'); return }
  if (!wizardForm.apiAddress.trim()) { Message.warning('请填写 API 地址'); return }
  wizardSaving.value = true
  try {
    const supportedPaths = 'images' // 图片对接统一只暴露 images 端点
    const allModels = [
      ...selectedPresetModels.value.map(m => ({ originalModelId: m, modelName: m, customModelId: modelBatchCustomId.value || m, weight: 1 })),
      ...manualModels.value.map(m => ({ originalModelId: m, modelName: m, customModelId: modelBatchCustomId.value || m, weight: 1 }))
    ]
    const apiKeys = keysTextarea.value.split('\n').map(k => k.trim()).filter(Boolean)
    const extConfig = wizardForm.supplierType === 'Xfyun' && wizardForm._xfyunAppId ? JSON.stringify({ appId: wizardForm._xfyunAppId }) : null
    const apiKey2 = wizardForm.supplierType === 'Xfyun' ? wizardForm._apiKey2 : null
    const supportedSizes = wizardForm._supportedSizeList.join(',')

    if (editingChannelId.value) {
      const existing = channels.value.find(ch => ch.id === editingChannelId.value)
      await channelApi.update(editingChannelId.value, {
        id: editingChannelId.value, name: wizardForm.name, remark: wizardForm.remark,
        supplierType: wizardForm.supplierType, apiAddress: wizardForm.apiAddress,
        timeoutSeconds: wizardForm.timeoutSeconds, cooldownSeconds: wizardForm.cooldownSeconds,
        protocolType: 'Chat', sseEnabled: false, supportedPaths, passthroughPaths: supportedPaths, fallbackTarget: 'Chat',
        extConfig, enabled: wizardForm.enabled, supportedSizes,
        // 空数组=不同步密钥（保留原值），传 null 会被 ASP.NET 必填校验拒掉
        apiKeys: apiKeys.length > 0 ? apiKeys.map(k => ({ keyValue: k, keyValue2: apiKey2, weight: 1, status: 1 })) : [],
        // 讯飞 APISecret 等第二密钥：顶层透传，后端收到后即便不动密钥列表也会更新现有所有密钥的 KeyValue2
        apiKey2,
        // 模型走全量同步（后端先删后插），避免每次保存重复累加；空数组=保留原值不动
        models: allModels.length > 0 ? allModels : []
      })
      Message.success('对接更新成功')
    } else {
      await channelApi.create({
        name: wizardForm.name, remark: wizardForm.remark, supplierType: wizardForm.supplierType, apiAddress: wizardForm.apiAddress,
        timeoutSeconds: wizardForm.timeoutSeconds, cooldownSeconds: wizardForm.cooldownSeconds,
        protocolType: 'Chat', supportedPaths, passthroughPaths: supportedPaths, fallbackTarget: 'Chat', sseEnabled: false, extConfig, supportedSizes,
        apiKeys, apiKey2, models: allModels
      })
      Message.success('对接创建成功')
    }
    wizardVisible.value = false
    await loadAll()
  } catch (e) { console.error(e) } finally { wizardSaving.value = false }
}

async function editChannel(record) {
  editingChannelId.value = record.id
  wizardVisible.value = true
  const preset = supplierPresets.value.find(p => p.type === record.supplierType)
  let xfyunAppId = ''
  try { if (record.extConfig) xfyunAppId = JSON.parse(record.extConfig).appId || '' } catch { /* ignore */ }
  const supportedSizeList = (record.supportedSizes || '').split(',').map(s => s.trim()).filter(Boolean)
  Object.assign(wizardForm, {
    name: record.name, remark: record.remark || '', supplierType: record.supplierType,
    apiAddress: record.apiAddress, timeoutSeconds: record.timeoutSeconds, cooldownSeconds: record.cooldownSeconds,
    enabled: record.enabled, _availableModels: preset?.defaultModels || [], _xfyunAppId: xfyunAppId, _apiKey2: '',
    _supportedSizeList: supportedSizeList
  })
  onSupplierChange()
  keysTextarea.value = ''
  manualModels.value = []
  manualModelInput.value = ''
  try {
    const [keysRes, modelsRes] = await Promise.all([channelApi.getKeys(record.id), channelApi.getModels(record.id)])
    existingKeys.value = keysRes.code === 200 ? (keysRes.data || []) : []
    existingModels.value = modelsRes.code === 200 ? (modelsRes.data || []) : []
    // 回填讯飞 APISecret（第二密钥）：从拉到的现有密钥取 KeyValue2，不依赖列表挂的 _existingKeys
    if (wizardForm.supplierType === 'Xfyun' && existingKeys.value.length > 0) {
      wizardForm._apiKey2 = existingKeys.value[0].keyValue2 || ''
    }
    // 回显勾选状态：推荐模型中已存在于渠道的，恢复勾选；不在推荐列表里的（手动加的）放回 manualModels
    const presetSet = new Set(wizardForm._availableModels)
    const existingIds = new Set(existingModels.value.map(m => m.originalModelId))
    selectedPresetModels.value = wizardForm._availableModels.filter(m => existingIds.has(m))
    manualModels.value = existingModels.value
      .filter(m => !presetSet.has(m.originalModelId))
      .map(m => m.originalModelId)
  } catch {
    existingKeys.value = []; existingModels.value = []
    selectedPresetModels.value = []; manualModels.value = []
  }
  // 上游测试：加载已有模型列表给下拉用，清空上次测试结果
  upstreamTestResult.value = null
  Object.assign(upstreamTestForm, { modelId: '', prompt: '一只柴犬，水彩风格', size: '1024x1024', responseFormat: 'url' })
  await loadUpstreamTestModels()
}

async function removeExistingKey(keyId) {
  try { await channelApi.deleteKey(editingChannelId.value, keyId); existingKeys.value = existingKeys.value.filter(k => k.id !== keyId); Message.success('已删除') } catch { /* ignore */ }
}

async function toggleChannel(record, val) {
  try { await channelApi.update(record.id, { ...record, enabled: val }); record.enabled = val; Message.success(val ? '已启用' : '已禁用') } catch { /* ignore */ }
}

async function deleteChannel(id) {
  await channelApi.delete(id); Message.success('删除成功'); await loadAll()
}

// ===== 对接详情 =====
const detailVisible = ref(false)
const detailChannel = ref(null)
const detailKeys = ref([])
const detailModels = ref([])
const newKeyValue = ref('')
const newModelForm = reactive({ originalModelId: '', modelName: '', customModelId: '', weight: 1 })
const keyColumns = [
  { title: 'ID', dataIndex: 'id', width: 50 }, { title: '密钥', dataIndex: 'keyValue', ellipsis: true, width: 300 },
  { title: '权重', dataIndex: 'weight', width: 60 }, { title: '状态', dataIndex: 'status', width: 70 }, { title: '操作', slotName: 'action', width: 80 }
]
const modelColumns = [
  { title: '原始模型ID', dataIndex: 'originalModelId', width: 150 }, { title: '显示名称', dataIndex: 'modelName', width: 120 },
  { title: '自定义模型ID', dataIndex: 'customModelId', width: 150 }, { title: '权重', dataIndex: 'weight', width: 60 }, { title: '操作', slotName: 'action', width: 80 }
]

async function manageChannelDetail(record) {
  detailChannel.value = record; detailVisible.value = true; newKeyValue.value = ''
  Object.assign(newModelForm, { originalModelId: '', modelName: '', customModelId: '', weight: 1 })
  await Promise.all([loadKeys(), loadModels()])
}
async function loadKeys() { if (!detailChannel.value) return; const res = await channelApi.getKeys(detailChannel.value.id); if (res.code === 200) detailKeys.value = res.data }
async function loadModels() { if (!detailChannel.value) return; const res = await channelApi.getModels(detailChannel.value.id); if (res.code === 200) detailModels.value = res.data }
async function addSingleKey() {
  if (!newKeyValue.value.trim()) return
  await channelApi.addKey(detailChannel.value.id, { keyValue: newKeyValue.value.trim(), weight: 1, status: 1 })
  Message.success('添加成功'); newKeyValue.value = ''; await loadKeys()
}
async function addSingleModel() {
  if (!newModelForm.originalModelId || !newModelForm.customModelId) { Message.warning('请填写原始模型ID和自定义模型ID'); return }
  await channelApi.addModel(detailChannel.value.id, { ...newModelForm })
  Message.success('添加成功'); Object.assign(newModelForm, { originalModelId: '', modelName: '', customModelId: '', weight: 1 }); await loadModels()
}
async function deleteModel(modelId) { await channelApi.deleteModel(detailChannel.value.id, modelId); Message.success('删除成功'); await loadModels() }
async function deleteKey(keyId) { await channelApi.deleteKey(detailChannel.value.id, keyId); Message.success('已删除'); await loadKeys() }

// ===== 下游接口测试（独立菜单，对外端点） =====
const downstreamTestVisible = ref(false)
const testRunning = ref(false)
const testForm = reactive({
  token: '',
  model: '',
  prompt: '一只柴犬，水彩风格',
  size: '1024x1024',
  responseFormat: 'url',
  image: ''
})
const testResult = ref(null) // { ok, code, duration, request, response, images: [{type, src}] }

function openDownstreamTest() {
  downstreamTestVisible.value = true
  testResult.value = null
}

async function runTest() {
  if (!testForm.token.trim()) { Message.warning('请填写令牌'); return }
  if (!testForm.model.trim()) { Message.warning('请填写模型 customModelId'); return }
  if (!testForm.prompt.trim()) { Message.warning('请填写提示词'); return }

  testRunning.value = true
  testResult.value = null
  const startTime = Date.now()

  // 构造请求体：image 字段支持多图（每行一个 URL 或 base64）
  const lines = testForm.image.split('\n').map(s => s.trim()).filter(Boolean)
  let imageField = undefined
  if (lines.length === 1) imageField = lines[0]
  else if (lines.length > 1) imageField = lines

  const payload = {
    model: testForm.model,
    prompt: testForm.prompt,
    size: testForm.size || undefined,
    response_format: testForm.responseFormat
  }
  if (imageField) payload.image = imageField

  try {
    const resp = await axios.post('/v1/images/generations', payload, {
      headers: { 'Authorization': `Bearer ${testForm.token}`, 'Content-Type': 'application/json' },
      timeout: 120000
    })
    // 提取图片列表
    const images = []
    const data = resp.data?.data || []
    for (const item of data) {
      if (item.url) images.push({ type: 'url', src: item.url })
      else if (item.b64_json) images.push({ type: 'b64', src: `data:image/png;base64,${item.b64_json}` })
    }
    testResult.value = {
      ok: true,
      code: resp.status,
      duration: Date.now() - startTime,
      request: JSON.stringify(payload, null, 2),
      response: JSON.stringify(resp.data, null, 2),
      images
    }
  } catch (e) {
    const respData = e.response?.data
    testResult.value = {
      ok: false,
      code: e.response?.status || 0,
      duration: Date.now() - startTime,
      request: JSON.stringify(payload, null, 2),
      response: JSON.stringify(respData || { error: e.message }, null, 2),
      images: []
    }
  } finally {
    testRunning.value = false
  }
}

// ===== 上游接口测试（编辑弹窗内，用渠道密钥直测上游） =====
const upstreamTestRunning = ref(false)
const upstreamTestForm = reactive({ modelId: '', prompt: '一只柴犬，水彩风格', size: '1024x1024', responseFormat: 'url' })
const upstreamTestResult = ref(null) // { ok, code, duration, upstreamUrl, request, response, images }
const upstreamTestModelOptions = ref([])

async function loadUpstreamTestModels() {
  if (!editingChannelId.value) return
  try {
    const res = await channelApi.getModels(editingChannelId.value)
    upstreamTestModelOptions.value = res.code === 200 ? (res.data || []).map(m => m.originalModelId) : []
  } catch { upstreamTestModelOptions.value = [] }
}

async function runUpstreamTest() {
  if (!editingChannelId.value) { Message.warning('请先保存对接后再测试'); return }
  if (!upstreamTestForm.modelId.trim()) { Message.warning('请选择原始模型ID'); return }
  if (!upstreamTestForm.prompt.trim()) { Message.warning('请填写提示词'); return }

  upstreamTestRunning.value = true
  upstreamTestResult.value = null
  const startTime = Date.now()

  try {
    const resp = await channelApi.testImageModel({
      channelId: editingChannelId.value,
      modelId: upstreamTestForm.modelId,
      prompt: upstreamTestForm.prompt,
      size: upstreamTestForm.size || null,
      responseFormat: upstreamTestForm.responseFormat,
      image: null
    })
    const d = resp.data || {}
    // 解析图片
    const images = []
    try {
      const body = JSON.parse(d.responseBody || '{}')
      const data = body.data || body.images || []
      for (const item of data) {
        if (item.url) images.push({ type: 'url', src: item.url })
        else if (item.b64_json) images.push({ type: 'b64', src: `data:image/png;base64,${item.b64_json}` })
      }
    } catch { /* ignore parse */ }
    upstreamTestResult.value = {
      ok: d.success === true,
      code: d.statusCode || 0,
      duration: Date.now() - startTime,
      upstreamUrl: d.upstreamUrl || '',
      request: d.requestBody || '',
      response: d.responseBody || (d.error ? JSON.stringify({ error: d.error }) : ''),
      images
    }
  } catch (e) {
    upstreamTestResult.value = {
      ok: false,
      code: e.response?.status || 0,
      duration: Date.now() - startTime,
      upstreamUrl: '',
      request: '',
      response: JSON.stringify(e.response?.data || { error: e.message }, null, 2),
      images: []
    }
  } finally {
    upstreamTestRunning.value = false
  }
}

function copyText(text) {
  navigator.clipboard?.writeText(text).then(() => Message.success('已复制')).catch(() => {})
}

// ===== 加载 =====
async function loadAll() {
  loading.value = true
  try {
    const [chRes, presetRes] = await Promise.all([channelApi.list(), channelApi.getSupplierPresets()])
    if (presetRes.code === 200) supplierPresets.value = (presetRes.data || []).filter(p => imageSupplierTypes.includes(p.type))
    if (chRes.code === 200) {
      // 图片转发页只展示图片渠道：supportedPaths 含 images 的归此处管，文本 LLM 渠道归渠道管理
      const list = (chRes.data || []).filter(ch => (ch.supportedPaths || '').split(',').map(s => s.trim()).includes('images'))
      for (const ch of list) {
        try { const d = await channelApi.getModels(ch.id); ch._modelCount = d.code === 200 ? (d.data?.length || 0) : 0 } catch { ch._modelCount = 0 }
      }
      // 按 ID 从小到大排序
      list.sort((a, b) => a.id - b.id)
      channels.value = list
    }
  } finally { loading.value = false }
}

onMounted(loadAll)
</script>

<style scoped>
.image-forward-page { }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 12px; }
.header-left { display: flex; align-items: baseline; gap: 12px; }
.page-header h2 { margin: 0; }
.page-subtitle { font-size: 13px; color: var(--color-text-3); }
.standard-card { margin-bottom: 16px; }
.table-card { margin-bottom: 16px; }
.form-hint { font-size: 12px; color: var(--color-text-3); margin-top: 2px; }
.wizard-footer { display: flex; justify-content: flex-end; gap: 8px; margin-top: 16px; padding-top: 12px; border-top: 1px solid var(--color-border-2); }
.existing-list { display: flex; flex-direction: column; gap: 6px; max-height: 160px; overflow-y: auto; padding: 8px; background: var(--color-fill-1); border-radius: 6px; }
.existing-item { display: flex; align-items: center; justify-content: space-between; padding: 4px 8px; background: var(--color-bg-2); border-radius: 4px; }
.existing-item .key-text { font-size: 12px; color: var(--color-text-2); background: var(--color-fill-2); padding: 2px 6px; border-radius: 3px; word-break: break-all; }

/* 接口测试面板 */
.test-panel { background: var(--color-fill-1); border-radius: 8px; padding: 12px; margin-top: 8px; }
.test-hint { font-size: 12px; color: var(--color-text-3); margin-bottom: 10px; }
.test-hint code { background: var(--color-fill-2); padding: 1px 6px; border-radius: 3px; color: rgb(var(--primary-6)); }
.test-section { margin-top: 12px; }
.test-section-title { font-size: 12px; font-weight: 500; color: var(--color-text-2); margin-bottom: 4px; display: flex; align-items: center; gap: 8px; }
.test-payload {
  font-size: 12px; font-family: 'Consolas', monospace;
  background: #1e1e1e; color: #d4d4d4;
  border-radius: 6px; padding: 10px 12px;
  white-space: pre-wrap; word-break: break-all; margin: 0;
  max-height: 240px; overflow-y: auto;
}
.test-images { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 8px; }
.test-image-item { display: flex; flex-direction: column; align-items: center; gap: 4px; }
.test-image-meta { display: flex; align-items: center; gap: 4px; }
</style>
