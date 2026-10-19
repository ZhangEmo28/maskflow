<script setup>
import { computed, inject, ref } from "vue";

const team = inject("team");
const account = inject("account");
const selectedProject = inject("selectedProject");
const canManageProject = inject("canManageProject");
const canReviewProject = inject("canReviewProject");
const roleLabels = inject("roleLabels");
const inviteMember = inject("inviteMember");
const changeMemberRole = inject("changeMemberRole");
const removeMember = inject("removeMember");
const revokeInvite = inject("revokeInvite");
const assignImages = inject("assignImages");

const selectedAssignees = ref([]);
const assignScope = ref("unassigned");
const editors = computed(() => [
  ...(team.owner ? [team.owner] : []),
  ...team.members.filter((member) => member.role !== "viewer")
]);

async function copyInviteLink() {
  await navigator.clipboard?.writeText(team.lastInviteUrl);
  team.status = "邀请链接已复制";
}
</script>

<template>
  <aside class="team-panel" role="dialog" aria-label="项目团队">
    <header class="team-panel-head">
      <div>
        <h2>项目团队</h2>
        <p>{{ selectedProject?.name }} · 我的角色：{{ roleLabels[selectedProject?.role || 'owner'] }}</p>
      </div>
      <button class="btn ghost compact-btn" type="button" @click="team.open = false">关闭</button>
    </header>
    <p class="team-hint">所有者管理成员；审核员可改标签、审核和导出；标注员可标注并提交审核；只读成员只能查看。图片和配额都记在所有者名下。</p>

    <ul class="team-list">
      <li v-if="team.owner">
        <div><strong>{{ team.owner.username || team.owner.email }}</strong><small>{{ team.owner.email }}</small></div>
        <span class="team-role">所有者</span>
      </li>
      <li v-for="member in team.members" :key="member.userId">
        <div><strong>{{ member.username || member.email }}</strong><small>{{ member.email }}</small></div>
        <select v-if="canManageProject" :value="member.role" @change="changeMemberRole(member, $event.target.value)">
          <option value="reviewer">审核员</option>
          <option value="annotator">标注员</option>
          <option value="viewer">只读</option>
        </select>
        <span v-else class="team-role">{{ roleLabels[member.role] }}</span>
        <button v-if="canManageProject || member.userId === account?.id" class="label-delete-btn" type="button" @click="removeMember(member)">
          {{ member.userId === account?.id ? '退出' : '移除' }}
        </button>
      </li>
      <li v-for="invite in team.invites" :key="invite.id" class="team-invite">
        <div><strong>{{ invite.email }}</strong><small>待接受邀请 · {{ roleLabels[invite.role] }}</small></div>
        <button class="label-delete-btn" type="button" @click="revokeInvite(invite)">撤销</button>
      </li>
    </ul>

    <section v-if="canManageProject" class="team-section">
      <h3>邀请成员</h3>
      <div class="team-invite-form">
        <input v-model="team.inviteEmail" type="email" placeholder="对方邮箱" @keyup.enter="inviteMember" />
        <select v-model="team.inviteRole">
          <option value="annotator">标注员</option>
          <option value="reviewer">审核员</option>
          <option value="viewer">只读</option>
        </select>
        <button class="btn compact-btn" type="button" :disabled="team.busy || !team.inviteEmail.trim()" @click="inviteMember">邀请</button>
      </div>
      <div v-if="team.lastInviteUrl" class="team-link">
        <input :value="team.lastInviteUrl" readonly @focus="$event.target.select()" />
        <button class="btn secondary compact-btn" type="button" @click="copyInviteLink">复制</button>
      </div>
    </section>

    <section v-if="canReviewProject && editors.length > 1" class="team-section">
      <h3>分配图片</h3>
      <div class="team-assignees">
        <label v-for="person in editors" :key="person.userId">
          <input v-model="selectedAssignees" type="checkbox" :value="person.userId" />
          {{ person.username || person.email }}
        </label>
      </div>
      <div class="team-invite-form">
        <select v-model="assignScope">
          <option value="unassigned">还没分配的图片</option>
          <option value="unannotated">还没标注的图片</option>
          <option value="all">全部图片（重新分配）</option>
        </select>
        <button class="btn compact-btn" type="button" :disabled="!selectedAssignees.length" @click="assignImages(selectedAssignees, assignScope)">平均分配</button>
        <button class="btn ghost compact-btn" type="button" @click="assignImages([], assignScope)">取消分配</button>
      </div>
    </section>

    <p v-if="team.status" class="team-status">{{ team.status }}</p>
  </aside>
</template>
