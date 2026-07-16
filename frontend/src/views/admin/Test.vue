<template>
  <div class="test-page">
    <div class="page-header">
      <div>
        <h2>接口测试</h2>
        <span class="page-subtitle">使用令牌 + 自定义模型ID，走完整下游链路验证 LLM 模型链与图片生成</span>
      </div>
    </div>

    <a-card :bordered="false" class="standard-card">
      <a-form :model="form" layout="vertical">
        <a-row :gutter="8">
          <a-col :span="8">
            <a-form-item label="测试类型" required>
              <a-radio-group v-model="form.type" @change="onTypeChange">
                <a-radio value="text">文本 LLM</a-radio>
                <a-radio value="image">图片生成</a-radio>
              </a-radio-group>
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="令牌" required>
              <a-input v-model="form.token" placeholder="sk-xxx" allow-clear />
            </a-form-item>
          </a-col>
          <a-col :span="8">
            <a-form-item label="自定义模型ID" required>
              <a-input v-model="form.model" placeholder="如 my-gpt4-pool / doubao-seedream" />
            </a-form-item>
          </a-col>
        </a-row>

        <!-- 文本 LLM 参数 -->
        <template v-if="form.type === 'text'">
          <a-row :gutter="8">
            <a-col :span="24">
              <a-form-item label="提示词（消息）" required>
                <a-textarea v-model="form.prompt" :rows="3" placeholder="如: 你好，请介绍一下你自己" />
              </a-form-item>
            </a-col>
          </a-row>
          <a-row :gutter="8">
            <a-col :span="8">
              <a-form-item label="流式 SSE">
                <a-switch v-model="form.stream" />
              </a-form-item>
            </a-col>
            <a-col :span="8">
              <a-form-item label="最大 tokens">
                <a-input-number v-model="form.maxTokens" :min="1" :max="8192" style="width:100%" />
              </a-form-item>
            </a-col>
            <a-col :span="8">
              <a-form-item label="温度">
                <a-input-number v-model="form.temperature" :min="0" :max="2" :step="0.1" style="width:100%" />
              </a-form-item>
            </a-col>
          </a-row>
        </template>

        <!-- 图片生成参数 -->
        <template v-else>
          <a-row :gutter="8">
            <a-col :span="14">
              <a-form-item label="提示词 prompt" required>
                <a-input v-model="form.imagePrompt" placeholder="如: 一只柴犬，水彩风格" />
              </a-form-item>
            </a-col>
            <a-col :span="6">
              <a-form-item label="尺寸 size">
                <a-input v-model="form.size" placeholder="1024x1024" />
              </a-form-item>
            </a-col>
            <a-col :span="4">
              <a-form-item label="返回格式">
                <a-select v-model="form.responseFormat">
                  <a-option value="url">url</a-option>
                  <a-option value="b64_json">b64_json</a-option>
                </a-select>
              </a-form-item>
            </a-col>
          </a-row>
          <a-form-item label="参考图 image (可选，URL 或 Base64，多图每行一个)">
            <a-textarea v-model="form.image" :rows="2" placeholder="https://example.com/ref.png
base64,iVBORw0KG..." />
          </a-form-item>
        </template>

        <a-row :gutter="8" align="center">
          <a-col :span="6">
            <a-button type="primary" size="large" :loading="running" @click="runTest" long>
              <template #icon><icon-experiment /></template>
              {{ running ? '测试中...' : '开始测试' }}
            </a-button>
          </a-col>
          <a-col :span="6">
            <a-button size="large" type="outline" @click="clearResult" long :disabled="!result && !streamBuffer">清空结果</a-button>
          </a-col>
        </a-row>
      </a-form>
    </a-card>

    <!-- 结果区 -->
    <a-card v-if="result || streamBuffer" :bordered="false" class="result-card" title="测试结果">
      <template #extra>
        <a-tag v-if="result" :color="result.ok ? 'green' : 'red'" size="small">
          {{ result.ok ? '成功' : '失败' }} · HTTP {{ result.code }} · {{ result.duration }}ms
        </a-tag>
        <a-tag v-if="streaming" color="arcoblue" size="small">流式接收中…</a-tag>
      </template>

      <div v-if="result || streamBuffer" class="result-section">
        <div class="result-section-title">▼ 发送内容</div>
        <pre class="result-payload">{{ sentPayload }}</pre>
      </div>

      <div v-if="form.type === 'text' && (result || streamBuffer)" class="result-section">
        <div class="result-section-title">▼ 模型响应</div>
        <pre class="result-text">{{ result ? (result.response || '') : '' }}{{ streamBuffer }}</pre>
      </div>

      <div v-if="form.type === 'image' && result" class="result-section">
        <div class="result-section-title">▼ 原始响应</div>
        <pre class="result-payload">{{ result.response }}</pre>
      </div>

      <div v-if="form.type === 'image' && result && result.images.length > 0" class="result-section">
        <div class="result-section-title">▼ 获取到的图片 ({{ result.images.length }} 张)</div>
        <div class="result-images">
          <div v-for="(img, idx) in result.images" :key="idx" class="result-image-item">
            <img :src="img.src" :alt="`图${idx+1}`" style="max-width:180px;max-height:180px;border:1px solid var(--color-border-2);border-radius:4px" @error="(e)=>e.target.style.display='none'" />
            <div class="result-image-meta">
              <a-tag size="small" :color="img.type === 'url' ? 'arcoblue' : 'orangered'">{{ img.type }}</a-tag>
              <a-button v-if="img.type === 'url'" size="mini" type="text" @click="copyText(img.src)">复制</a-button>
            </div>
          </div>
        </div>
      </div>
    </a-card>
  </div>
</template>

<script setup>
import { ref, reactive } from 'vue'
import { Message } from '@arco-design/web-vue'
import { IconExperiment } from '@arco-design/web-vue/es/icon'
import axios from 'axios'

const form = reactive({
  type: 'text',
  token: '',
  model: '',
  // 文本
  prompt: '你好，请介绍一下你自己',
  stream: false,
  maxTokens: 512,
  temperature: 0.7,
  // 图片
  imagePrompt: '一只柴犬，水彩风格',
  size: '1024x1024',
  responseFormat: 'url',
  image: ''
})

const running = ref(false)
const streaming = ref(false)
const streamBuffer = ref('')
const result = ref(null) // { ok, code, duration, response, images }
const sentPayload = ref('')

function onTypeChange() {
  clearResult()
}

function clearResult() {
  result.value = null
  streamBuffer.value = ''
  sentPayload.value = ''
}

async function runTest() {
  if (!form.token.trim()) { Message.warning('请填写令牌'); return }
  if (!form.model.trim()) { Message.warning('请填写自定义模型ID'); return }
  if (form.type === 'text' && !form.prompt.trim()) { Message.warning('请填写提示词'); return }
  if (form.type === 'image' && !form.imagePrompt.trim()) { Message.warning('请填写提示词'); return }

  running.value = true
  result.value = null
  streamBuffer.value = ''
  const startTime = Date.now()

  if (form.type === 'text') {
    await runTextTest(startTime)
  } else {
    await runImageTest(startTime)
  }
}

async function runTextTest(startTime) {
  const payload = {
    model: form.model,
    messages: [{ role: 'user', content: form.prompt }],
    max_tokens: form.maxTokens,
    temperature: form.temperature,
    stream: form.stream
  }
  sentPayload.value = JSON.stringify(payload, null, 2)

  try {
    if (form.stream) {
      streaming.value = true
      const resp = await fetch('/v1/chat/completions', {
        method: 'POST',
        headers: { 'Authorization': `Bearer ${form.token}`, 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      })
      if (!resp.ok) {
        const errBody = await resp.text()
        result.value = {
          ok: false, code: resp.status, duration: Date.now() - startTime,
          response: errBody, images: []
        }
        return
      }
      const reader = resp.body.getReader()
      const decoder = new TextDecoder('utf-8')
      let buf = ''
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() || ''
        for (const line of lines) {
          const trimmed = line.trim()
          if (!trimmed.startsWith('data:')) continue
          const data = trimmed.slice(5).trim()
          if (data === '[DONE]') continue
          try {
            const json = JSON.parse(data)
            const delta = json.choices?.[0]?.delta?.content || ''
            if (delta) streamBuffer.value += delta
          } catch { /* ignore parse */ }
        }
      }
      result.value = {
        ok: true, code: 200, duration: Date.now() - startTime,
        response: '', images: []
      }
    } else {
      const resp = await axios.post('/v1/chat/completions', payload, {
        headers: { 'Authorization': `Bearer ${form.token}`, 'Content-Type': 'application/json' },
        timeout: 120000
      })
      const content = resp.data?.choices?.[0]?.message?.content || resp.data?.choices?.[0]?.text || ''
      result.value = {
        ok: true, code: resp.status, duration: Date.now() - startTime,
        response: content || JSON.stringify(resp.data, null, 2), images: []
      }
    }
  } catch (e) {
    const respData = e.response?.data
    result.value = {
      ok: false, code: e.response?.status || 0, duration: Date.now() - startTime,
      response: JSON.stringify(respData || { error: e.message }, null, 2), images: []
    }
  } finally {
    streaming.value = false
    running.value = false
  }
}

async function runImageTest(startTime) {
  const lines = form.image.split('\n').map(s => s.trim()).filter(Boolean)
  let imageField = undefined
  if (lines.length === 1) imageField = lines[0]
  else if (lines.length > 1) imageField = lines

  const payload = {
    model: form.model,
    prompt: form.imagePrompt,
    size: form.size || undefined,
    response_format: form.responseFormat
  }
  if (imageField) payload.image = imageField
  sentPayload.value = JSON.stringify(payload, null, 2)

  try {
    const resp = await axios.post('/v1/images/generations', payload, {
      headers: { 'Authorization': `Bearer ${form.token}`, 'Content-Type': 'application/json' },
      timeout: 120000
    })
    const images = []
    const data = resp.data?.data || []
    for (const item of data) {
      if (item.url) images.push({ type: 'url', src: item.url })
      else if (item.b64_json) images.push({ type: 'b64', src: `data:image/png;base64,${item.b64_json}` })
    }
    result.value = {
      ok: true, code: resp.status, duration: Date.now() - startTime,
      response: JSON.stringify(resp.data, null, 2), images
    }
  } catch (e) {
    const respData = e.response?.data
    result.value = {
      ok: false, code: e.response?.status || 0, duration: Date.now() - startTime,
      response: JSON.stringify(respData || { error: e.message }, null, 2), images: []
    }
  } finally {
    running.value = false
  }
}

function copyText(text) {
  navigator.clipboard?.writeText(text).then(() => Message.success('已复制')).catch(() => {})
}
</script>

<style scoped>
.test-page { }
.page-header { display: flex; justify-content: space-between; align-items: center; margin-bottom: 16px; flex-wrap: wrap; gap: 12px; }
.page-header h2 { margin: 0; }
.page-subtitle { font-size: 13px; color: var(--color-text-3); }
.standard-card { margin-bottom: 16px; }
.result-card { margin-top: 16px; }
.result-section { margin-top: 12px; }
.result-section-title { font-size: 12px; font-weight: 500; color: var(--color-text-2); margin-bottom: 4px; }
.result-payload {
  font-size: 12px; font-family: 'Consolas', monospace;
  background: #1e1e1e; color: #d4d4d4;
  border-radius: 6px; padding: 10px 12px;
  white-space: pre-wrap; word-break: break-all; margin: 0;
  max-height: 300px; overflow-y: auto;
}
.result-text {
  font-size: 13px; font-family: 'Consolas', monospace;
  background: #1e1e1e; color: #d4d4d4;
  border-radius: 6px; padding: 10px 12px;
  white-space: pre-wrap; word-break: break-word; margin: 0;
  max-height: 400px; overflow-y: auto;
}
.result-images { display: flex; flex-wrap: wrap; gap: 12px; margin-top: 8px; }
.result-image-item { display: flex; flex-direction: column; align-items: center; gap: 4px; }
.result-image-meta { display: flex; align-items: center; gap: 4px; }
</style>
