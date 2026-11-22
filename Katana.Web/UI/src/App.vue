<script setup lang="ts">
import KatanaEditor from './editor/KatanaEditor.vue';
import { api } from './api';
</script>

<template>
  <KatanaEditor :api="api" />
</template>
