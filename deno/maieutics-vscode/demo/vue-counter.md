---
title: 计数器按钮
---

# 计数器按钮

<script setup lang="ts">
import { ref, computed } from 'vue'
const count = ref(0)
const isZero = computed(() => count.value === 0)
const increase = () => { count.value++ }
const decrease = () => { if (!isZero.value) count.value-- }
const reset = () => { count.value = 0 }
</script>

<p>
  <button class="counter-btn" @click="decrease" :disabled="isZero">−</button>
  <button class="counter-btn" @click="increase">+</button>
  <span class="counter-display">当前计数：{{ count }}</span>
  <button class="counter-reset" @click="reset" :disabled="isZero">重置</button>
</p>

<p class="counter-hint" v-if="isZero">还没有点击过，点一下 <strong>+</strong> 试试。</p>
<p class="counter-hint" v-else>已点击 {{ count }} 次。</p>

::: tip 说明 `{{ count }}` 是 Vue 模板插值，会随按钮实时更新。 :::

<style scoped>
.counter-btn { width: 36px; height: 36px; margin-right: 8px; border: 1px solid var(--vp-c-brand-1); border-radius: 8px; background: var(--vp-c-bg-soft); color: var(--vp-c-text-1); cursor: pointer; }
.counter-btn:hover:not(:disabled) { background: var(--vp-c-brand-soft); }
.counter-btn:disabled, .counter-reset:disabled { opacity: .45; cursor: not-allowed; }
.counter-display { margin: 0 12px 0 4px; font-variant-numeric: tabular-nums; }
.counter-reset { padding: 6px 12px; border: 1px solid var(--vp-c-divider); border-radius: 8px; background: transparent; color: var(--vp-c-text-2); cursor: pointer; }
.counter-hint { color: var(--vp-c-text-2); font-size: 14px; }
</style>
