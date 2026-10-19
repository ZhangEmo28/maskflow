<script setup>
import { inject, onMounted, reactive } from "vue";
import { apiFetch } from "../../lib/api";

const account = inject("account");
const go = inject("go");

// The authorization request arrives as the query string forwarded by /oauth/authorize.
const params = new URLSearchParams(window.location.search);
const request = {
  clientId: params.get("client_id"),
  redirectUri: params.get("redirect_uri"),
  responseType: params.get("response_type"),
  codeChallenge: params.get("code_challenge"),
  codeChallengeMethod: params.get("code_challenge_method"),
  state: params.get("state"),
  scope: params.get("scope")
};
const consent = reactive({ loading: true, busy: false, error: "", info: null });

onMounted(async () => {
  try {
    consent.info = await apiFetch(`/api/oauth/authorize-info?${params}`);
  } catch (error) {
    consent.error = error.message;
  } finally {
    consent.loading = false;
  }
});

async function decide(approve) {
  consent.busy = true;
  try {
    const data = await apiFetch("/api/oauth/approve", { method: "POST", body: { ...request, approve } });
    window.location.href = data.redirect;
  } catch (error) {
    consent.error = error.message;
    consent.busy = false;
  }
}
</script>

<template>
  <main class="oauth-page">
    <section class="oauth-card">
      <div class="oauth-brand"><span class="logo-mark">M</span><strong>MaskFlow</strong></div>
      <p v-if="consent.loading" class="oauth-muted">正在读取授权请求…</p>
      <template v-else-if="consent.error">
        <h1>无法授权</h1>
        <p class="oauth-error">{{ consent.error }}</p>
        <button class="btn secondary" type="button" @click="go('/dashboard.html')">返回控制台</button>
      </template>
      <template v-else>
        <h1>授权 {{ consent.info.clientName }} 访问 MaskFlow</h1>
        <p class="oauth-muted">登录账户：<strong>{{ account?.email }}</strong></p>
        <p>授权后，这个智能体可以代表你：</p>
        <ul class="oauth-scopes">
          <li>查看和管理你可以访问的项目、图片与标注</li>
          <li>运行 AI 自动标注（消耗项目所有者的 AI 额度）</li>
          <li>检查标注质量、发起模型训练、评估模型和导出数据集</li>
        </ul>
        <p class="oauth-muted">授权后将返回 <strong>{{ consent.info.redirectHost }}</strong>。只在你信任这个应用、并且是你本人发起连接时才授权。可随时在「账户设置 → 智能体连接」中断开。</p>
        <div class="oauth-actions">
          <button class="btn secondary" type="button" :disabled="consent.busy" @click="decide(false)">拒绝</button>
          <button class="btn" type="button" :disabled="consent.busy" @click="decide(true)">允许访问</button>
        </div>
      </template>
    </section>
  </main>
</template>
